import hljs from 'highlight.js/lib/core';
import bash from 'highlight.js/lib/languages/bash';
import csharp from 'highlight.js/lib/languages/csharp';
import css from 'highlight.js/lib/languages/css';
import go from 'highlight.js/lib/languages/go';
import java from 'highlight.js/lib/languages/java';
import javascript from 'highlight.js/lib/languages/javascript';
import json from 'highlight.js/lib/languages/json';
import markdown from 'highlight.js/lib/languages/markdown';
import powershell from 'highlight.js/lib/languages/powershell';
import python from 'highlight.js/lib/languages/python';
import scss from 'highlight.js/lib/languages/scss';
import sql from 'highlight.js/lib/languages/sql';
import typescript from 'highlight.js/lib/languages/typescript';
import xml from 'highlight.js/lib/languages/xml';
import yaml from 'highlight.js/lib/languages/yaml';

const languages = { bash, csharp, css, go, java, javascript, json, markdown, powershell, python, scss, sql, typescript, xml, yaml };
for (const [name, definition] of Object.entries(languages)) {
  hljs.registerLanguage(name, definition);
}

const languageByExtension: Record<string, string> = {
  cs: 'csharp', csx: 'csharp',
  ts: 'typescript', tsx: 'typescript', mts: 'typescript',
  js: 'javascript', jsx: 'javascript', mjs: 'javascript', cjs: 'javascript',
  json: 'json', jsonc: 'json',
  html: 'xml', htm: 'xml', xml: 'xml', csproj: 'xml', props: 'xml', targets: 'xml', config: 'xml', xaml: 'xml', cshtml: 'xml', razor: 'xml', svg: 'xml',
  css: 'css', scss: 'scss',
  sql: 'sql',
  py: 'python',
  go: 'go',
  java: 'java',
  md: 'markdown',
  yml: 'yaml', yaml: 'yaml',
  sh: 'bash', bash: 'bash',
  ps1: 'powershell', psm1: 'powershell',
};

export function languageFor(path: string): string | null {
  const extension = path.slice(path.lastIndexOf('.') + 1).toLowerCase();
  return languageByExtension[extension] ?? null;
}

/** Highlights one fragment in isolation; multi-line constructs lose context, which is acceptable for diffs. */
export function highlightFragment(text: string, language: string | null): string | null {
  if (!language || text.length === 0) {
    return null;
  }
  return hljs.highlight(text, { language, ignoreIllegals: true }).value;
}
