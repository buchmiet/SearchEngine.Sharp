mod glob_match;
mod natural_sort;

use glob_match::glob_match;
use natural_sort::natural_key;
use serde::Deserialize;
use std::collections::HashMap;
use std::env;
use std::fs::{self, File};
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::time::Instant;
use tantivy::collector::TopDocs;
use tantivy::query::{Query, RegexQuery};
use tantivy::schema::{
    Field, IndexRecordOption, Schema, TextFieldIndexing, TextOptions, Value, FAST, INDEXED, STORED,
};
use tantivy::{doc, Index, IndexWriter, ReloadPolicy, TantivyDocument};

#[derive(Debug, Clone, Deserialize)]
struct CorpusDocument {
    id: i32,
    name: String,
    #[serde(rename = "sizeBytes")]
    size_bytes: i64,
}

#[derive(Debug, Deserialize)]
struct CorpusFile {
    #[serde(rename = "exactQuery")]
    exact_query: String,
    documents: Vec<CorpusDocument>,
}

#[derive(Debug, Deserialize)]
struct WorkloadSpec {
    id: String,
    kind: String,
    query: Option<String>,
    #[serde(rename = "queryFromCorpus")]
    query_from_corpus: Option<String>,
    sort: String,
    #[serde(rename = "facetMinSize")]
    facet_min_size: Option<i64>,
    #[serde(rename = "facetMaxSize")]
    facet_max_size: Option<i64>,
    #[serde(rename = "coldIndex")]
    cold_index: bool,
}

#[derive(Debug, Deserialize)]
struct WorkloadsFile {
    #[serde(rename = "corpusFile")]
    corpus_file: String,
    #[serde(rename = "warmupIterations")]
    warmup_iterations: usize,
    #[serde(rename = "measureIterations")]
    measure_iterations: usize,
    workloads: Vec<WorkloadSpec>,
}

struct BenchRow {
    implementation: String,
    workload: String,
    hit_count: usize,
    median_ns: f64,
    mean_ns: f64,
    notes: String,
}

struct IndexedCorpus {
    index: Index,
    id_field: Field,
    name_field: Field,
    by_id: HashMap<i32, CorpusDocument>,
}

fn main() {
    let root = find_root().expect("workloads.json not found");
    let impl_name = env::var("SE_BENCH_IMPL").unwrap_or_else(|_| "tantivy".into());
    let output_dir = env::var("SE_BENCH_OUTPUT")
        .map(PathBuf::from)
        .unwrap_or_else(|_| root.join("results").join(hostname()));

    let workloads: WorkloadsFile =
        serde_json::from_str(&fs::read_to_string(root.join("workloads.json")).unwrap()).unwrap();
    let corpus: CorpusFile =
        serde_json::from_str(&fs::read_to_string(root.join(&workloads.corpus_file)).unwrap())
            .unwrap();

    fs::create_dir_all(&output_dir).unwrap();
    let mut rows = Vec::new();
    for workload in &workloads.workloads {
        let query = resolve_query(&corpus, workload);
        let (hit_count, timings, notes) = measure(&corpus, &workloads, workload, &query);
        rows.push(to_row(&impl_name, &workload.id, hit_count, timings, &notes));
    }

    let csv_path = output_dir.join(format!("{}-library-benchmark.csv", sanitize(&impl_name)));
    write_csv(&csv_path, &rows);
    eprintln!("Wrote {}", csv_path.display());
    for row in &rows {
        eprintln!(
            "{:<16} {:<32} hits={:>6} median={:>10.1} µs  ({})",
            row.implementation,
            row.workload,
            row.hit_count,
            row.median_ns / 1000.0,
            row.notes
        );
    }
}

fn measure(
    corpus: &CorpusFile,
    config: &WorkloadsFile,
    workload: &WorkloadSpec,
    query: &str,
) -> (usize, Vec<u128>, String) {
    let mut timings = Vec::with_capacity(config.measure_iterations);
    let mut hit_count = 0;
    let notes = if workload.cold_index {
        "cold-index"
    } else {
        "hot-index"
    }
    .to_string();

    let mut hot: Option<IndexedCorpus> = None;
    if !workload.cold_index {
        hot = Some(build_index(corpus));
    }

    let total = config.warmup_iterations + config.measure_iterations;
    for i in 0..total {
        let measure = i >= config.warmup_iterations;
        let start = Instant::now();
        let owned_cold;
        let indexed: &IndexedCorpus = if workload.cold_index {
            owned_cold = build_index(corpus);
            &owned_cold
        } else {
            hot.as_ref().unwrap()
        };
        hit_count = execute(indexed, workload, query);
        let elapsed = start.elapsed().as_nanos();
        if measure {
            timings.push(elapsed);
        }
    }

    (hit_count, timings, notes)
}

fn build_index(corpus: &CorpusFile) -> IndexedCorpus {
    let mut schema_builder = Schema::builder();
    let text_indexing = TextFieldIndexing::default()
        .set_tokenizer("raw")
        .set_index_option(IndexRecordOption::Basic);
    let text_options = TextOptions::default()
        .set_indexing_options(text_indexing)
        .set_stored();
    let id_field = schema_builder.add_u64_field("id", STORED | INDEXED | FAST);
    let name_field = schema_builder.add_text_field("name", text_options);
    let _size_field = schema_builder.add_i64_field("size", STORED | INDEXED | FAST);
    let schema = schema_builder.build();

    let index = Index::create_in_ram(schema);
    index
        .tokenizers()
        .register("raw", tantivy::tokenizer::RawTokenizer::default());

    let mut writer: IndexWriter = index.writer(50_000_000).unwrap();
    for doc in &corpus.documents {
        writer
            .add_document(doc!(
                id_field => doc.id as u64,
                name_field => doc.name.as_str(),
                _size_field => doc.size_bytes,
            ))
            .unwrap();
    }
    writer.commit().unwrap();

    let by_id = corpus
        .documents
        .iter()
        .cloned()
        .map(|d| (d.id, d))
        .collect();

    IndexedCorpus {
        index,
        id_field,
        name_field,
        by_id,
    }
}

fn execute(indexed: &IndexedCorpus, workload: &WorkloadSpec, query: &str) -> usize {
    let reader = indexed
        .index
        .reader_builder()
        .reload_policy(ReloadPolicy::Manual)
        .try_into()
        .unwrap();
    reader.reload().unwrap();
    let searcher = reader.searcher();

    let mut ids = match workload.kind.as_str() {
        "within" | "within_facet" => {
            let pattern = format!("(?i).*{}.*", regex::escape(query));
            let regex_query: Box<dyn Query> =
                Box::new(RegexQuery::from_pattern(&pattern, indexed.name_field).unwrap());
            let top_docs = searcher
                .search(&regex_query, &TopDocs::with_limit(200_000))
                .unwrap();
            let mut ids = Vec::with_capacity(top_docs.len());
            for (_score, addr) in top_docs {
                let doc: TantivyDocument = searcher.doc(addr).unwrap();
                if let Some(id) = doc.get_first(indexed.id_field).and_then(|v| v.as_u64()) {
                    ids.push(id as i32);
                }
            }
            ids
        }
        "exact" => indexed
            .by_id
            .values()
            .filter(|d| d.name == query)
            .map(|d| d.id)
            .collect(),
        "glob" => indexed
            .by_id
            .values()
            .filter(|d| glob_match(query, &d.name))
            .map(|d| d.id)
            .collect(),
        other => panic!("unsupported kind {other}"),
    };

    if workload.kind == "within_facet" {
        let min = workload.facet_min_size.unwrap_or(1_024);
        let max = workload.facet_max_size.unwrap_or(1_048_576);
        ids.retain(|id| {
            indexed
                .by_id
                .get(id)
                .map(|d| d.size_bytes >= min && d.size_bytes <= max)
                .unwrap_or(false)
        });
    }

    sort_results(&mut ids, &indexed.by_id, &workload.sort);
    ids.len()
}

fn sort_results(ids: &mut [i32], by_id: &HashMap<i32, CorpusDocument>, sort: &str) {
    if sort == "natural" {
        ids.sort_by(|a, b| {
            natural_key(&by_id[a].name).cmp(&natural_key(&by_id[b].name))
        });
    } else {
        ids.sort_unstable();
    }
}

fn resolve_query(corpus: &CorpusFile, workload: &WorkloadSpec) -> String {
    if let Some(q) = &workload.query {
        return q.clone();
    }
    match workload.query_from_corpus.as_deref() {
        Some("exactQuery") => corpus.exact_query.clone(),
        other => panic!("unknown queryFromCorpus {other:?}"),
    }
}

fn to_row(
    impl_name: &str,
    workload: &str,
    hit_count: usize,
    mut timings: Vec<u128>,
    notes: &str,
) -> BenchRow {
    timings.sort_unstable();
    let median = timings[timings.len() / 2] as f64;
    let mean = timings.iter().map(|t| *t as f64).sum::<f64>() / timings.len() as f64;
    BenchRow {
        implementation: impl_name.to_string(),
        workload: workload.to_string(),
        hit_count,
        median_ns: median,
        mean_ns: mean,
        notes: notes.to_string(),
    }
}

fn write_csv(path: &Path, rows: &[BenchRow]) {
    let file = File::create(path).unwrap();
    let mut w = BufWriter::new(file);
    writeln!(w, "implementation,workload,hit_count,median_ns,mean_ns,notes").unwrap();
    for row in rows {
        writeln!(
            w,
            "{},{},{},{:.0},{:.0},\"{}\"",
            row.implementation, row.workload, row.hit_count, row.median_ns, row.mean_ns, row.notes
        )
        .unwrap();
    }
}

fn find_root() -> Option<PathBuf> {
    let mut dir = env::current_dir().ok()?;
    loop {
        if dir.join("workloads.json").exists() {
            return Some(dir);
        }
        if !dir.pop() {
            break;
        }
    }
    None
}

fn hostname() -> String {
    env::var("COMPUTERNAME")
        .or_else(|_| env::var("HOSTNAME"))
        .unwrap_or_else(|_| "local".into())
        .to_lowercase()
}

fn sanitize(value: &str) -> String {
    value.replace(['/', '\\', ':'], "-")
}
