import { createWriteStream, mkdirSync, readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { hostname } from "node:os";
import MiniSearch from "minisearch";
import { globMatch } from "./glob-match.mjs";
import { naturalKey } from "./natural-sort.mjs";

const implName = process.env.SE_BENCH_IMPL ?? "minisearch";
const root = findRoot();
const outputDir =
  process.env.SE_BENCH_OUTPUT ??
  join(root, "results", hostname().toLowerCase());
mkdirSync(outputDir, { recursive: true });

const workloads = JSON.parse(readFileSync(join(root, "workloads.json"), "utf8"));
const corpus = JSON.parse(
  readFileSync(join(root, workloads.corpusFile), "utf8"),
);

/** @type {Array<{implementation:string,workload:string,hit_count:number,median_ns:number,mean_ns:number,notes:string}>} */
const rows = [];

for (const workload of workloads.workloads) {
  const query = resolveQuery(corpus, workload);
  const { hitCount, timings, notes } = measure(corpus, workloads, workload, query);
  rows.push(toRow(implName, workload.id, hitCount, timings, notes));
}

const csvPath = join(outputDir, `${implName}-library-benchmark.csv`);
writeCsv(csvPath, rows);
console.log(`Wrote ${csvPath}`);
for (const row of rows) {
  console.log(
    `${row.implementation.padEnd(16)} ${row.workload.padEnd(32)} hits=${String(row.hit_count).padStart(6)} median=${(row.median_ns / 1000).toFixed(1).padStart(10)} µs  (${row.notes})`,
  );
}

function measure(corpus, config, workload, query) {
  /** @type {number[]} */
  const timings = [];
  let hitCount = 0;
  const notes = workload.coldIndex ? "cold-index" : "hot-index";
  /** @type {ReturnType<typeof buildIndex> | null} */
  let hot = workload.coldIndex ? null : buildIndex(corpus);
  const total = config.warmupIterations + config.measureIterations;
  for (let i = 0; i < total; i++) {
    const measureIter = i >= config.warmupIterations;
    const indexed = workload.coldIndex ? buildIndex(corpus) : hot;
    const start = process.hrtime.bigint();
    hitCount = execute(indexed, workload, query);
    const elapsed = Number(process.hrtime.bigint() - start);
    if (measureIter) timings.push(elapsed);
  }
  return { hitCount, timings, notes };
}

function buildIndex(corpus) {
  const docs = corpus.documents.map((d) => ({
    id: d.id,
    name: d.name,
    sizeBytes: d.sizeBytes,
  }));
  const byId = new Map(docs.map((d) => [d.id, d]));
  const mini = new MiniSearch({
    idField: "id",
    fields: ["name"],
    storeFields: ["name", "sizeBytes"],
    tokenize: (text) => bigrams(text.toLowerCase()),
    processTerm: (term) => term,
  });
  mini.addAll(docs);
  return { mini, byId, docs };
}

function execute(indexed, workload, query) {
  let ids;
  switch (workload.kind) {
    case "within":
    case "within_facet":
      ids = searchWithin(indexed, query);
      break;
    case "exact":
      ids = indexed.docs.filter((d) => d.name === query).map((d) => d.id);
      break;
    case "glob":
      ids = indexed.docs
        .filter((d) => globMatch(query, d.name))
        .map((d) => d.id);
      break;
    default:
      throw new Error(`unsupported kind ${workload.kind}`);
  }

  if (workload.kind === "within_facet") {
    const min = workload.facetMinSize ?? 1_024;
    const max = workload.facetMaxSize ?? 1_048_576;
    ids = ids.filter((id) => {
      const doc = indexed.byId.get(id);
      return doc && doc.sizeBytes >= min && doc.sizeBytes <= max;
    });
  }

  sortResults(ids, indexed, workload.sort);
  return ids.length;
}

function searchWithin(indexed, query) {
  const q = query.toLowerCase();
  const results = indexed.mini.search(q, {
    combineWith: "AND",
    prefix: false,
    fuzzy: 0,
    tokenize: (text) => bigrams(text.toLowerCase()),
  });
  return results
    .filter((r) => r.name.toLowerCase().includes(q))
    .map((r) => r.id);
}

function bigrams(text) {
  /** @type {string[]} */
  const tokens = [];
  if (text.length < 2) return tokens;
  for (let i = 0; i < text.length - 1; i++) {
    tokens.push(text.slice(i, i + 2));
  }
  return tokens;
}

function sortResults(ids, indexed, sort) {
  if (sort === "natural") {
    ids.sort(
      (a, b) =>
        naturalKey(indexed.byId.get(a).name).localeCompare(
          naturalKey(indexed.byId.get(b).name),
        ),
    );
  } else {
    ids.sort((a, b) => a - b);
  }
}

function resolveQuery(corpus, workload) {
  if (workload.query) return workload.query;
  if (workload.queryFromCorpus === "exactQuery") return corpus.exactQuery;
  throw new Error(`unknown queryFromCorpus ${workload.queryFromCorpus}`);
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
