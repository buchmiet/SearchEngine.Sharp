use serde::Deserialize;
use std::env;
use std::fs::{self, File};
use std::io::{BufWriter, Write};
use std::path::{Path, PathBuf};
use std::time::Instant;

#[derive(Debug, Deserialize)]
struct CorpusDocument {
    id: i32,
    name: String,
    sizeBytes: i64,
    modifiedTicks: i64,
}

#[derive(Debug, Deserialize)]
struct CorpusFile {
    documentCount: i32,
    seed: i32,
    withinQuery: String,
    exactQuery: String,
    globQuery: String,
    zeroHitQuery: String,
    facetMinSize: i64,
    facetMaxSize: i64,
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
    facetMinSize: Option<i64>,
    facetMaxSize: Option<i64>,
    #[serde(default)]
    coldIndex: bool,
    #[serde(default)]
    requiresFacet: bool,
    #[serde(default)]
    competitorsOnly: bool,
}

#[derive(Debug, Deserialize)]
struct WorkloadsFile {
    corpusFile: String,
    warmupIterations: usize,
    measureIterations: usize,
    workloads: Vec<WorkloadSpec>,
}

struct BenchRow {
    implementation: String,
    workload: String,
    hit_count: i32,
    median_ns: f64,
    mean_ns: f64,
    notes: String,
}

fn main() {
    let root = find_root().expect("workloads.json not found");
    let impl_name = env::var("SE_BENCH_IMPL").unwrap_or_else(|_| "rust-scan".into());
    let output_dir = env::var("SE_BENCH_OUTPUT").map(PathBuf::from).unwrap_or_else(|_| {
        root.join("results").join(
            env::var("COMPUTERNAME")
                .or_else(|_| env::var("HOSTNAME"))
                .unwrap_or_else(|_| "local".into())
                .to_lowercase(),
        )
    });
    fs::create_dir_all(&output_dir).unwrap();

    let workloads: WorkloadsFile =
        serde_json::from_str(&fs::read_to_string(root.join("workloads.json")).unwrap()).unwrap();
    let corpus_path = root.join(&workloads.corpusFile);
    let corpus: CorpusFile =
        serde_json::from_str(&fs::read_to_string(&corpus_path).unwrap()).unwrap();

    let mut rows = Vec::new();
    for workload in &workloads.workloads {
        if workload.competitorsOnly {
            continue;
        }
        if workload.requiresFacet && workload.kind == "glob" {
            // glob handled below
        }
        if workload.kind == "glob" {
            // supported via scan/fnmatch
        }
        let query = resolve_query(&corpus, workload);
        let notes = if workload.coldIndex {
            "hot-corpus,no-index-rebuild"
        } else {
            "hot-corpus"
        };
        let (hit_count, timings) = measure(&corpus, workload, &query, &workloads, &impl_name);
        rows.push(to_row(&impl_name, &workload.id, hit_count, timings, notes));
    }

    for workload in workloads
        .workloads
        .iter()
        .filter(|w| w.competitorsOnly)
    {
        let query = resolve_query(&corpus, workload);
        let (hit_count, timings) = measure(&corpus, workload, &query, &workloads, &impl_name);
        rows.push(to_row(
            &impl_name,
            &workload.id,
            hit_count,
            timings,
            "linear-scan",
        ));
    }

    let csv_path = output_dir.join(format!("{}-competitor-benchmark.csv", sanitize(&impl_name)));
    write_csv(&csv_path, &rows);
    println!("Wrote {}", csv_path.display());
    for row in &rows {
        println!(
            "{:<24} {:<32} hits={:>6} median={:>10.1} µs  ({})",
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
    workload: &WorkloadSpec,
    query: &str,
    config: &WorkloadsFile,
    impl_name: &str,
) -> (i32, Vec<u128>) {
    let mut timings = Vec::with_capacity(config.measureIterations);
    let mut hit_count = 0;
    let total = config.warmupIterations + config.measureIterations;
    for i in 0..total {
        let measure = i >= config.warmupIterations;
        let start = Instant::now();
        hit_count = execute(corpus, workload, query, impl_name);
        let elapsed = start.elapsed().as_nanos();
        if measure {
            timings.push(elapsed);
        }
    }
    (hit_count, timings)
}

fn execute(corpus: &CorpusFile, workload: &WorkloadSpec, query: &str, impl_name: &str) -> i32 {
    match workload.kind.as_str() {
        "within" | "within_facet" => {
            let min = workload.facetMinSize.unwrap_or(corpus.facetMinSize);
            let max = workload.facetMaxSize.unwrap_or(corpus.facetMaxSize);
            let facet = workload.kind == "within_facet";
            let mut hits: Vec<i32> = corpus
                .documents
                .iter()
                .filter(|d| {
                    d.name.to_ascii_lowercase().contains(&query.to_ascii_lowercase())
                        && (!facet || (d.sizeBytes >= min && d.sizeBytes <= max))
                })
                .map(|d| d.id)
                .collect();
            if workload.sort == "natural" {
                hits.sort_by(|a, b| {
                    let na = corpus.documents.iter().find(|d| d.id == *a).unwrap();
                    let nb = corpus.documents.iter().find(|d| d.id == *b).unwrap();
                    natural_key(&na.name).cmp(&natural_key(&nb.name))
                });
            }
            hits.len() as i32
        }
        "exact" => corpus
            .documents
            .iter()
            .filter(|d| d.name == query)
            .count() as i32,
        "glob" => corpus
            .documents
            .iter()
            .filter(|d| glob_match(query, &d.name))
            .count() as i32,
        "naive_within" => corpus
            .documents
            .iter()
            .filter(|d| d.name.to_ascii_lowercase().contains(&query.to_ascii_lowercase()))
            .count() as i32,
        "naive_within_facet_natural" => {
            let min = workload.facetMinSize.unwrap_or(corpus.facetMinSize);
            let max = workload.facetMaxSize.unwrap_or(corpus.facetMaxSize);
            let mut hits: Vec<&CorpusDocument> = corpus
                .documents
                .iter()
                .filter(|d| {
                    d.name.to_ascii_lowercase().contains(&query.to_ascii_lowercase())
                        && d.sizeBytes >= min
                        && d.sizeBytes <= max
                })
                .collect();
            hits.sort_by(|a, b| natural_key(&a.name).cmp(&natural_key(&b.name)));
            hits.len() as i32
        }
        _ => panic!("unsupported kind {}", workload.kind),
    }
}

fn resolve_query(corpus: &CorpusFile, workload: &WorkloadSpec) -> String {
    if let Some(q) = &workload.query {
        return q.clone();
    }
    match workload.query_from_corpus.as_deref() {
        Some("exactQuery") => corpus.exactQuery.clone(),
        other => panic!("unknown queryFromCorpus {:?}", other),
    }
}

fn glob_match(pattern: &str, text: &str) -> bool {
    glob_match_impl(pattern.as_bytes(), text.as_bytes())
}

fn glob_match_impl(mut pattern: &[u8], mut text: &[u8]) -> bool {
    while !pattern.is_empty() {
        if pattern[0] == b'*' {
            pattern = &pattern[1..];
            if pattern.is_empty() {
                return true;
            }
            for i in 0..=text.len() {
                if glob_match_impl(pattern, &text[i..]) {
                    return true;
                }
            }
            return false;
        }
        if text.is_empty() {
            return false;
        }
        if pattern[0] == b'?' || pattern[0] == text[0] {
            pattern = &pattern[1..];
            text = &text[1..];
        } else {
            return false;
        }
    }
    text.is_empty()
}

fn natural_key(text: &str) -> String {
    const PAD: usize = 12;
    let mut out = String::new();
    let mut first = true;
    let bytes = text.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        let c = bytes[i] as char;
        if matches!(c, '-' | ' ' | '_' | '/') {
            i += 1;
            continue;
        }
        if !first {
            out.push('|');
        }
        first = false;
        if c.is_ascii_digit() {
            let start = i;
            i += 1;
            while i < bytes.len() && (bytes[i] as char).is_ascii_digit() {
                i += 1;
            }
            out.push_str("0:");
            let digits = &text[start..i];
            for _ in digits.len()..PAD {
                out.push('0');
            }
            out.push_str(digits);
        } else if c.is_ascii_alphabetic() {
            let start = i;
            i += 1;
            while i < bytes.len() && (bytes[i] as char).is_ascii_alphabetic() {
                i += 1;
            }
            out.push_str("1:");
            out.push_str(&text[start..i].to_ascii_lowercase());
        } else {
            out.push_str("1:");
            out.push(c.to_ascii_lowercase());
            i += 1;
        }
    }
    out
}

fn to_row(impl_name: &str, workload: &str, hit_count: i32, mut timings: Vec<u128>, notes: &str) -> BenchRow {
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

fn sanitize(value: &str) -> String {
    value.replace(['/', '\\', ':'], "-")
}
