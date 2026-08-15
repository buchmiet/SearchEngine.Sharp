import { createWriteStream, mkdirSync, readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { hostname } from "node:os";

const implName = process.env.SE_BENCH_IMPL ?? "node-scan";
const root = findRoot();
const outputDir =
  process.env.SE_BENCH_OUTPUT ??
  join(root, "results", hostname().toLowerCase());
mkdirSync(outputDir, { recursive: true });

const config = JSON.parse(readFileSync(join(root, "workloads.json"), "utf8"));
const corpus = JSON.parse(
  readFileSync(join(root, config.corpusFile), "utf8"),
);

/** @type {Array<{implementation:string,workload:string,hit_count:number,median_ns:number,mean_ns:number,notes:string}>} */
const rows = [];

for (const workload of config.workloads) {
  if (workload.competitorsOnly) continue;
  const query = resolveQuery(corpus, workload);
  const notes = workload.coldIndex
    ? "hot-corpus,no-index-rebuild"
    : "hot-corpus";
  const { hitCount, timings } = measure(corpus, workload, query, config);
  rows.push(toRow(implName, workload.id, hitCount, timings, notes));
}

for (const workload of config.workloads.filter((w) => w.competitorsOnly)) {
  const query = resolveQuery(corpus, workload);
  const { hitCount, timings } = measure(corpus, workload, query, config);
  rows.push(toRow(implName, workload.id, hitCount, timings, "linear-scan"));
}

const csvPath = join(outputDir, `${sanitize(implName)}-competitor-benchmark.csv`);
writeCsv(csvPath, rows);
console.log(`Wrote ${csvPath}`);
for (const row of rows) {
  console.log(
    `${row.implementation.padEnd(24)} ${row.workload.padEnd(32)} hits=${String(row.hit_count).padStart(6)} median=${(row.median_ns / 1000).toFixed(1).padStart(10)} µs  (${row.notes})`,
  );
}

function measure(corpus, workload, query, config) {
  /** @type {number[]} */
  const timings = [];
  let hitCount = 0;
  const total = config.warmupIterations + config.measureIterations;
  for (let i = 0; i < total; i++) {
    const measureIter = i >= config.warmupIterations;
    const start = process.hrtime.bigint();
    hitCount = execute(corpus, workload, query);
    const elapsed = Number(process.hrtime.bigint() - start);
    if (measureIter) timings.push(elapsed);
  }
  return { hitCount, timings };
}

function execute(corpus, workload, query) {
  switch (workload.kind) {
    case "within":
    case "within_facet": {
      const min = workload.facetMinSize ?? corpus.facetMinSize;
      const max = workload.facetMaxSize ?? corpus.facetMaxSize;
      const facet = workload.kind === "within_facet";
      const q = query.toLowerCase();
      /** @type {number[]} */
      let hits = [];
      for (const doc of corpus.documents) {
        if (
          doc.name.toLowerCase().includes(q) &&
          (!facet || (doc.sizeBytes >= min && doc.sizeBytes <= max))
        ) {
          hits.push(doc.id);
        }
      }
      if (workload.sort === "natural") {
        hits.sort((a, b) =>
          naturalKey(nameForId(corpus, a)).localeCompare(
            naturalKey(nameForId(corpus, b)),
          ),
        );
      }
      return hits.length;
    }
    case "exact":
      return corpus.documents.filter((d) => d.name === query).length;
    case "glob":
      return corpus.documents.filter((d) => globMatch(query, d.name)).length;
    case "naive_within":
      return corpus.documents.filter((d) =>
        d.name.toLowerCase().includes(query.toLowerCase()),
      ).length;
    case "naive_within_facet_natural": {
      const min = workload.facetMinSize ?? corpus.facetMinSize;
      const max = workload.facetMaxSize ?? corpus.facetMaxSize;
      const q = query.toLowerCase();
      const names = corpus.documents
        .filter(
          (d) =>
            d.name.toLowerCase().includes(q) &&
            d.sizeBytes >= min &&
            d.sizeBytes <= max,
        )
        .map((d) => d.name)
        .sort((a, b) => naturalKey(a).localeCompare(naturalKey(b)));
      return names.length;
    }
    default:
      throw new Error(`unsupported kind ${workload.kind}`);
  }
}

function nameForId(corpus, id) {
  return corpus.documents.find((d) => d.id === id)?.name ?? "";
}

function resolveQuery(corpus, workload) {
  if (workload.query) return workload.query;
  if (workload.queryFromCorpus === "exactQuery") return corpus.exactQuery;
  throw new Error(`unknown queryFromCorpus ${workload.queryFromCorpus}`);
}

function globMatch(pattern, text) {
  return globMatchBytes([...pattern], [...text]);
}

function globMatchBytes(pattern, text) {
  while (pattern.length > 0) {
    if (pattern[0] === "*") {
      pattern = pattern.slice(1);
      if (pattern.length === 0) return true;
      for (let i = 0; i <= text.length; i++) {
        if (globMatchBytes(pattern.slice(), text.slice(i))) return true;
      }
      return false;
    }
    if (text.length === 0) return false;
    if (pattern[0] === "?" || pattern[0] === text[0]) {
      pattern = pattern.slice(1);
      text = text.slice(1);
    } else {
      return false;
    }
  }
  return text.length === 0;
}

function naturalKey(text) {
  const pad = 12;
  /** @type {string[]} */
  const parts = [];
  let first = true;
  for (let i = 0; i < text.length; ) {
    const c = text[i];
    if ("- _/".includes(c)) {
      i++;
      continue;
    }
    if (!first) parts.push("|");
    first = false;
    if (c >= "0" && c <= "9") {
      let start = i;
      i++;
      while (i < text.length && text[i] >= "0" && text[i] <= "9") i++;
      const digits = text.slice(start, i);
      parts.push("0:" + digits.padStart(pad, "0"));
    } else if (/[A-Za-z]/.test(c)) {
      let start = i;
      i++;
      while (i < text.length && /[A-Za-z]/.test(text[i])) i++;
      parts.push("1:" + text.slice(start, i).toLowerCase());
    } else {
      parts.push("1:" + c.toLowerCase());
      i++;
    }
  }
  return parts.join("");
}

function toRow(implName, workload, hitCount, timings, notes) {
  timings.sort((a, b) => a - b);
  const mean = timings.reduce((a, b) => a + b, 0) / timings.length;
  return {
    implementation: implName,
    workload,
    hit_count: hitCount,
    median_ns: timings[Math.floor(timings.length / 2)],
    mean_ns: mean,
    notes,
  };
}

function writeCsv(path, rows) {
  const stream = createWriteStream(path);
  stream.write("implementation,workload,hit_count,median_ns,mean_ns,notes\n");
  for (const row of rows) {
    stream.write(
      `${row.implementation},${row.workload},${row.hit_count},${row.median_ns.toFixed(0)},${row.mean_ns.toFixed(0)},"${row.notes}"\n`,
    );
  }
  stream.end();
}

function findRoot() {
  let dir = dirname(fileURLToPath(import.meta.url));
  while (true) {
    if (existsSync(join(dir, "workloads.json"))) return dir;
    const parent = dirname(dir);
    if (parent === dir) throw new Error("workloads.json not found");
    dir = parent;
  }
}

function sanitize(value) {
  return value.replaceAll(/[/\\:]/g, "-");
}
