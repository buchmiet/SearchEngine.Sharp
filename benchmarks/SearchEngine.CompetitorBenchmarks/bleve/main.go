package main

import (
	"encoding/csv"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/blevesearch/bleve/v2"
	"github.com/blevesearch/bleve/v2/analysis/analyzer/keyword"
	"github.com/blevesearch/bleve/v2/mapping"
	"github.com/blevesearch/bleve/v2/search/query"
)

type corpusDocument struct {
	ID        int    `json:"id"`
	Name      string `json:"name"`
	SizeBytes int64  `json:"sizeBytes"`
}

type corpusFile struct {
	ExactQuery string           `json:"exactQuery"`
	Documents  []corpusDocument `json:"documents"`
}

type workloadSpec struct {
	ID              string `json:"id"`
	Kind            string `json:"kind"`
	Query           string `json:"query"`
	QueryFromCorpus string `json:"queryFromCorpus"`
	Sort            string `json:"sort"`
	FacetMinSize    int64  `json:"facetMinSize"`
	FacetMaxSize    int64  `json:"facetMaxSize"`
	ColdIndex       bool   `json:"coldIndex"`
}

type workloadsFile struct {
	CorpusFile        string         `json:"corpusFile"`
	WarmupIterations  int            `json:"warmupIterations"`
	MeasureIterations int            `json:"measureIterations"`
	Workloads         []workloadSpec `json:"workloads"`
}

type benchRow struct {
	Implementation string
	Workload       string
	HitCount       int
	MedianNs       float64
	MeanNs         float64
	Notes          string
}

type indexedCorpus struct {
	index bleve.Index
	byID  map[int]corpusDocument
}

func main() {
	root := findRoot()
	implName := envOr("SE_BENCH_IMPL", "bleve")
	outputDir := envOr("SE_BENCH_OUTPUT", filepath.Join(root, "results", hostname()))
	_ = os.MkdirAll(outputDir, 0o755)

	var config workloadsFile
	must(json.Unmarshal(mustRead(filepath.Join(root, "workloads.json")), &config))
	var corpus corpusFile
	must(json.Unmarshal(mustRead(filepath.Join(root, config.CorpusFile)), &corpus))

	var rows []benchRow
	for _, workload := range config.Workloads {
		q := resolveQuery(&corpus, &workload)
		hits, timings, notes := measure(&corpus, &config, &workload, q)
		rows = append(rows, toRow(implName, workload.ID, hits, timings, notes))
	}

	csvPath := filepath.Join(outputDir, sanitize(implName)+"-library-benchmark.csv")
	writeCSV(csvPath, rows)
	fmt.Printf("Wrote %s\n", csvPath)
	for _, row := range rows {
		fmt.Printf("%-16s %-32s hits=%6d median=%10.1f µs  (%s)\n",
			row.Implementation, row.Workload, row.HitCount, row.MedianNs/1000.0, row.Notes)
	}
}

func measure(corpus *corpusFile, config *workloadsFile, workload *workloadSpec, queryText string) (int, []int64, string) {
	notes := "hot-index"
	if workload.ColdIndex {
		notes = "cold-index"
	}
	var hot *indexedCorpus
	if !workload.ColdIndex {
		hot = buildIndex(corpus)
	}
	var timings []int64
	hits := 0
	total := config.WarmupIterations + config.MeasureIterations
	for i := 0; i < total; i++ {
		measure := i >= config.WarmupIterations
		var indexed *indexedCorpus
		if workload.ColdIndex {
			indexed = buildIndex(corpus)
		} else {
			indexed = hot
		}
		start := time.Now()
		hits = execute(indexed, workload, queryText)
		elapsed := time.Since(start).Nanoseconds()
		if measure {
			timings = append(timings, elapsed)
		}
	}
	return hits, timings, notes
}

func buildIndex(corpus *corpusFile) *indexedCorpus {
	indexMapping := bleve.NewIndexMapping()
	docMapping := mapping.NewDocumentMapping()
	nameMapping := mapping.NewTextFieldMapping()
	nameMapping.Analyzer = keyword.Name
	sizeMapping := mapping.NewNumericFieldMapping()
	docMapping.AddFieldMappingsAt("name", nameMapping)
	docMapping.AddFieldMappingsAt("size", sizeMapping)
	indexMapping.AddDocumentMapping("doc", docMapping)
	indexMapping.DefaultMapping = docMapping

	index, err := bleve.NewMemOnly(indexMapping)
	must(err)

	batch := index.NewBatch()
	for _, doc := range corpus.Documents {
		must(batch.Index(fmt.Sprintf("%d", doc.ID), map[string]any{
			"name": doc.Name,
			"size": doc.SizeBytes,
		}))
	}
	must(index.Batch(batch))

	byID := make(map[int]corpusDocument, len(corpus.Documents))
	for _, doc := range corpus.Documents {
		byID[doc.ID] = doc
	}
	return &indexedCorpus{index: index, byID: byID}
}

func execute(indexed *indexedCorpus, workload *workloadSpec, queryText string) int {
	var ids []int
	switch workload.Kind {
	case "within", "within_facet":
		pattern := "(?i).*" + regexpEscape(queryText) + ".*"
		q := bleve.NewRegexpQuery(pattern)
		q.SetField("name")
		ids = searchIDs(indexed, q)
	case "exact":
		for _, doc := range indexed.byID {
			if doc.Name == queryText {
				ids = append(ids, doc.ID)
			}
		}
	case "glob":
		for _, doc := range indexed.byID {
			if globMatch(queryText, doc.Name) {
				ids = append(ids, doc.ID)
			}
		}
	default:
		panic("unsupported kind " + workload.Kind)
	}

	if workload.Kind == "within_facet" {
		min := workload.FacetMinSize
		max := workload.FacetMaxSize
		if min == 0 {
			min = 1024
		}
		if max == 0 {
			max = 1048576
		}
		filtered := ids[:0]
		for _, id := range ids {
			doc := indexed.byID[id]
			if doc.SizeBytes >= min && doc.SizeBytes <= max {
				filtered = append(filtered, id)
			}
		}
		ids = filtered
	}

	sortResults(ids, indexed, workload.Sort)
	return len(ids)
}

func searchIDs(indexed *indexedCorpus, q query.Query) []int {
	search := bleve.NewSearchRequest(q)
	search.Size = 200000
	search.Fields = []string{}
	res, err := indexed.index.Search(search)
	must(err)
	ids := make([]int, 0, len(res.Hits))
	for _, hit := range res.Hits {
		var id int
		fmt.Sscanf(hit.ID, "%d", &id)
		ids = append(ids, id)
	}
	return ids
}

func sortResults(ids []int, indexed *indexedCorpus, sortMode string) {
	if sortMode == "natural" {
		sort.Slice(ids, func(i, j int) bool {
			return naturalKey(indexed.byID[ids[i]].Name) < naturalKey(indexed.byID[ids[j]].Name)
		})
	} else {
		sort.Ints(ids)
	}
}

func resolveQuery(corpus *corpusFile, workload *workloadSpec) string {
	if workload.Query != "" {
		return workload.Query
	}
	if workload.QueryFromCorpus == "exactQuery" {
		return corpus.ExactQuery
	}
	panic("unknown queryFromCorpus")
}

func toRow(implName, workload string, hitCount int, timings []int64, notes string) benchRow {
	sort.Slice(timings, func(i, j int) bool { return timings[i] < timings[j] })
	var sum int64
	for _, t := range timings {
		sum += t
	}
	return benchRow{
		Implementation: implName,
		Workload:       workload,
		HitCount:       hitCount,
		MedianNs:       float64(timings[len(timings)/2]),
		MeanNs:         float64(sum) / float64(len(timings)),
		Notes:          notes,
	}
}

func writeCSV(path string, rows []benchRow) {
	f, err := os.Create(path)
	must(err)
	defer f.Close()
	w := csv.NewWriter(f)
	must(w.Write([]string{"implementation", "workload", "hit_count", "median_ns", "mean_ns", "notes"}))
	for _, row := range rows {
		must(w.Write([]string{
			row.Implementation,
			row.Workload,
			fmt.Sprintf("%d", row.HitCount),
			fmt.Sprintf("%.0f", row.MedianNs),
			fmt.Sprintf("%.0f", row.MeanNs),
			row.Notes,
		}))
	}
	w.Flush()
	must(w.Error())
}

func globMatch(pattern, word string) bool {
	p := []byte(pattern)
	w := []byte(word)
	patternIndex := 0
	wordIndex := 0
	starPatternIndex := -1
	starWordIndex := 0

	for wordIndex < len(w) {
		if patternIndex < len(p) {
			pc := p[patternIndex]
			if pc == '*' {
				starPatternIndex = patternIndex
				starWordIndex = wordIndex
				patternIndex++
				for patternIndex < len(p) && p[patternIndex] == '*' {
					patternIndex++
				}
				continue
			}
			if pc == '?' || pc == w[wordIndex] {
				patternIndex++
				wordIndex++
				continue
			}
		}
		if starPatternIndex >= 0 {
			starWordIndex++
			wordIndex = starWordIndex
			patternIndex = starPatternIndex + 1
			for patternIndex < len(p) && p[patternIndex] == '*' {
				patternIndex++
			}
			continue
		}
		return false
	}
	for patternIndex < len(p) && p[patternIndex] == '*' {
		patternIndex++
	}
	return patternIndex == len(p)
}

func naturalKey(sortText string) string {
	const pad = 12
	var b strings.Builder
	first := true
	runes := []rune(sortText)
	for i := 0; i < len(runes); {
		c := runes[i]
		if c == '-' || c == ' ' || c == '_' || c == '/' {
			i++
			continue
		}
		if !first {
			b.WriteByte('|')
		}
		first = false
		if c >= '0' && c <= '9' {
			start := i
			i++
			for i < len(runes) && runes[i] >= '0' && runes[i] <= '9' {
				i++
			}
			b.WriteString("0:")
			digits := string(runes[start:i])
			for p := len(digits); p < pad; p++ {
				b.WriteByte('0')
			}
			b.WriteString(digits)
		} else if (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') {
			start := i
			i++
			for i < len(runes) && ((runes[i] >= 'a' && runes[i] <= 'z') || (runes[i] >= 'A' && runes[i] <= 'Z')) {
				i++
			}
			b.WriteString("1:")
			b.WriteString(strings.ToLower(string(runes[start:i])))
		} else {
			b.WriteString("1:")
			b.WriteString(strings.ToLower(string(c)))
			i++
		}
	}
	return b.String()
}

func regexpEscape(s string) string {
	replacer := strings.NewReplacer(
		"\\", "\\\\", ".", "\\.", "+", "\\+", "*", "\\*", "?", "\\?", "(", "\\(", ")", "\\)",
		"[", "\\[", "]", "\\]", "{", "\\{", "}", "\\}", "^", "\\^", "$", "\\$", "|", "\\|",
	)
	return replacer.Replace(s)
}

func findRoot() string {
	dir, err := os.Getwd()
	must(err)
	for {
		if _, err := os.Stat(filepath.Join(dir, "workloads.json")); err == nil {
			return dir
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			panic("workloads.json not found")
		}
		dir = parent
	}
}

func mustRead(path string) []byte {
	b, err := os.ReadFile(path)
	must(err)
	return b
}

func must(err error) {
	if err != nil {
		panic(err)
	}
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func hostname() string {
	h, err := os.Hostname()
	if err != nil {
		return "local"
	}
	return strings.ToLower(h)
}

func sanitize(value string) string {
	return strings.NewReplacer("/", "-", "\\", "-", ":", "-").Replace(value)
}
