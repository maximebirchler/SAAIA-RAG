import fs from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";

const artifactToolPath =
  process.env.ARTIFACT_TOOL_PATH ??
  "C:/Users/MBirchler/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/@oai/artifact-tool/dist/artifact_tool.mjs";

const { SpreadsheetFile, Workbook } = await import(pathToFileURL(artifactToolPath).href);

const repoRoot = process.cwd();
const workbookSpecs = [
  {
    input: path.join(repoRoot, "artifacts/manual-test-checklist/SAAIA_Bench_Matrix_v3.1.tsv"),
    output: path.join(repoRoot, "artifacts/manual-test-checklist/SAAIA_Bench_Matrix_v3.1.xlsx"),
    sheetName: "Bench Matrix",
  },
  {
    input: path.join(repoRoot, "artifacts/manual-test-checklist/SAAIA_Bench_Results_2026-04-24.tsv"),
    output: path.join(repoRoot, "artifacts/manual-test-checklist/SAAIA_Bench_Results_2026-04-24.xlsx"),
    sheetName: "Bench Results",
  },
];

function columnName(index) {
  let name = "";
  let n = index + 1;
  while (n > 0) {
    const rem = (n - 1) % 26;
    name = String.fromCharCode(65 + rem) + name;
    n = Math.floor((n - 1) / 26);
  }
  return name;
}

async function readTsv(filePath) {
  const raw = await fs.readFile(filePath, "utf8");
  return raw
    .replace(/^\uFEFF/, "")
    .split(/\r?\n/)
    .filter((line) => line.length > 0)
    .map((line) => line.split("\t"));
}

async function exportWorkbook(spec) {
  const rows = await readTsv(spec.input);
  if (rows.length === 0) {
    throw new Error(`No rows found in ${spec.input}`);
  }

  const width = Math.max(...rows.map((row) => row.length));
  const normalized = rows.map((row) => [
    ...row,
    ...Array.from({ length: width - row.length }, () => ""),
  ]);

  const workbook = Workbook.create();
  const sheet = workbook.worksheets.add(spec.sheetName);
  const range = sheet.getRange(`A1:${columnName(width - 1)}${normalized.length}`);
  range.values = normalized;

  const output = await SpreadsheetFile.exportXlsx(workbook);
  await output.save(spec.output);
  console.log(`wrote ${spec.output}`);
}

for (const spec of workbookSpecs) {
  await exportWorkbook(spec);
}
