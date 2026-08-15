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
	"unicode"
)

type corpusDocument struct {
	ID            int    `json:"id"`
	Name          string `json:"name"`
	SizeBytes     int64  `json:"sizeBytes"`
	ModifiedTicks int64  `json:"modifiedTicks"`
}

type corpusFile struct {
	DocumentCount int              `json:"documentCount"`
	Seed          int              `json:"seed"`
	WithinQuery   string           `json:"withinQuery"`
	ExactQuery    string           `json:"exactQuery"`
	GlobQuery     string           `json:"globQuery"`
	ZeroHitQuery  string           `json:"zeroHitQuery"`
	FacetMinSize  int64            `json:"facetMinSize"`
	FacetMaxSize  int64            `json:"facetMaxSize"`
	Documents     []corpusDocument `json:"documents"`
}

type workloadSpec struct {
	ID               string `json:"id"`
	Kind             string `json:"kind"`
	Query            string `json:"query"`
	QueryFromCorpus  string `json:"queryFromCorpus"`
	Sort             string `json:"sort"`
	FacetMinSize     int64  `json:"facetMinSize"`
	FacetMaxSize     int64  `json:"facetMaxSize"`
	ColdIndex        bool   `json:"coldIndex"`
	RequiresFacet    bool   `json:"requiresFacet"`
	CompetitorsOnly  bool   `json:"competitorsOnly"`
}

type workloadsFile struct {
	CorpusFile         string         `json:"corpusFile"`
	WarmupIterations   int            `json:"warmupIterations"`
	MeasureIterations  int            `json:"measureIterations"`
	Workloads          []workloadSpec `json:"workloads"`
}

type benchRow struct {
	Implementation string
	Workload       string
	HitCount       int
	MedianNs       float64
	MeanNs         float64
	Notes          string
}

func main() {
	root := findRoot()
	implName := envOr("SE_BENCH_IMPL", "go-scan")
	outputDir := envOr("SE_BENCH_OUTPUT", filepath.Join(root, "results", strings.ToLower(hostname())))
	_ = os.MkdirAll(outputDir, 0o755)

	var config workloadsFile
	must(json.Unmarshal(mustRead(filepath.Join(root, "workloads.json")), &config))
	var corpus corpusFile
	must(json.Unmarshal(mustRead(filepath.Join(root, config.CorpusFile)), &corpus))

	var rows []benchRow
	for _, workload := range config.Workloads {
		if workload.CompetitorsOnly {
			continue
		}
		query := resolveQuery(&corpus, &workload)
		notes := "hot-corpus"
		if workload.ColdIndex {
			notes = "hot-corpus,no-index-rebuild"
		}
		hits, timings := measure(&corpus, &workload, query, &config)
		rows = append(rows, toRow(implName, workload.ID, hits, timings, notes))
	}
	for _, workload := range config.Workloads {
		if !workload.CompetitorsOnly {
			continue
		}
		query := resolveQuery(&corpus, &workload)
		hits, timings := measure(&corpus, &workload, query, &config)
		rows = append(rows, toRow(implName, workload.ID, hits, timings, "linear-scan"))
	}

	csvPath := filepath.Join(outputDir, sanitize(implName)+"-competitor-benchmark.csv")
	writeCSV(csvPath, rows)
	fmt.Printf("Wrote %s\n", csvPath)
	for _, row := range rows {
		fmt.Printf("%-24s %-32s hits=%6d median=%10.1f µs  (%s)\n",
			row.Implementation, row.Workload, row.HitCount, row.MedianNs/1000.0, row.Notes)
	}
}

func measure(corpus *corpusFile, workload *workloadSpec, query string, config *workloadsFile) (int, []int64) {
	var timings []int64
	hits := 0
	total := config.WarmupIterations + config.MeasureIterations
	for i := 0; i < total; i++ {
		measure := i >= config.WarmupIterations
		start := time.Now()
		hits = execute(corpus, workload, query)
		elapsed := time.Since(start).Nanoseconds()
		if measure {
			timings = append(timings, elapsed)
		}
	}
	return hits, timings
}

func execute(corpus *corpusFile, workload *workloadSpec, query string) int {
	switch workload.Kind {
	case "within", "within_facet":
		min := workload.FacetMinSize
		max := workload.FacetMaxSize
		if min == 0 {
			min = corpus.FacetMinSize
		}
		if max == 0 {
			max = corpus.FacetMaxSize
		}
		facet := workload.Kind == "within_facet"
		q := strings.ToLower(query)
		var hits []int
		for _, doc := range corpus.Documents {
			if strings.Contains(strings.ToLower(doc.Name), q) {
				if !facet || (doc.SizeBytes >= min && doc.SizeBytes <= max) {
					hits = append(hits, doc.ID)
				}
			}
		}
		if workload.Sort == "natural" {
			sort.Slice(hits, func(i, j int) bool {
				return naturalKey(nameForID(corpus, hits[i])) < naturalKey(nameForID(corpus, hits[j]))
			})
		}
		return len(hits)
	case "exact":
		count := 0
		for _, doc := range corpus.Documents {
			if doc.Name == query {
				count++
			}
		}
		return count
	case "glob":
		count := 0
		for _, doc := range corpus.Documents {
			if globMatch(query, doc.Name) {
				count++
			}
		}
		return count
	case "naive_within":
		q := strings.ToLower(query)
		count := 0
		for _, doc := range corpus.Documents {
			if strings.Contains(strings.ToLower(doc.Name), q) {
				count++
			}
		}
		return count
	case "naive_within_facet_natural":
		min := workload.FacetMinSize
		max := workload.FacetMaxSize
		if min == 0 {
			min = corpus.FacetMinSize
		}
		if max == 0 {
			max = corpus.FacetMaxSize
		}
		q := strings.ToLower(query)
		var names []string
		for _, doc := range corpus.Documents {
			if strings.Contains(strings.ToLower(doc.Name), q) && doc.SizeBytes >= min && doc.SizeBytes <= max {
				names = append(names, doc.Name)
			}
		}
		sort.Slice(names, func(i, j int) bool { return naturalKey(names[i]) < naturalKey(names[j]) })
		return len(names)
	default:
		panic("unsupported kind " + workload.Kind)
	}
}

func nameForID(corpus *corpusFile, id int) string {
	for _, doc := range corpus.Documents {
		if doc.ID == id {
			return doc.Name
		}
	}
	return ""
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

func globMatch(pattern, text string) bool {
	return globMatchBytes([]byte(pattern), []byte(text))
}

func globMatchBytes(pattern, text []byte) bool {
	for len(pattern) > 0 {
		if pattern[0] == '*' {
			pattern = pattern[1:]
			if len(pattern) == 0 {
				return true
			}
			for i := 0; i <= len(text); i++ {
				if globMatchBytes(pattern, text[i:]) {
					return true
				}
			}
			return false
		}
		if len(text) == 0 {
			return false
		}
		if pattern[0] == '?' || pattern[0] == text[0] {
			pattern = pattern[1:]
			text = text[1:]
		} else {
			return false
		}
	}
	return len(text) == 0
}

func naturalKey(text string) string {
	const pad = 12
	var b strings.Builder
	first := true
	runes := []rune(text)
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
		if unicode.IsDigit(c) {
			start := i
			i++
			for i < len(runes) && unicode.IsDigit(runes[i]) {
				i++
			}
			b.WriteString("0:")
			digits := string(runes[start:i])
			for p := len(digits); p < pad; p++ {
				b.WriteByte('0')
			}
			b.WriteString(digits)
		} else if unicode.IsLetter(c) {
			start := i
			i++
			for i < len(runes) && unicode.IsLetter(runes[i]) {
				i++
			}
			b.WriteString("1:")
			b.WriteString(strings.ToLower(string(runes[start:i])))
		} else {
			b.WriteString("1:")
			b.WriteRune(unicode.ToLower(c))
			i++
		}
	}
	return b.String()
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
	return h
}

func sanitize(value string) string {
	replacer := strings.NewReplacer("/", "-", "\\", "-", ":", "-")
	return replacer.Replace(value)
}
