// Syntax-checks the browser code that ships inside ferahla.exe: every web/js module and the HTML report's inline
// script. Also enforces the CSP rule that the UI never uses innerHTML/outerHTML/insertAdjacentHTML or inline handlers.
// Usage: node tools/check-web.mjs   (exit 1 on any problem)
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const problems = [];

const walk = d => fs.readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(d, e.name)) : [path.join(d, e.name)]);
for (const f of walk(path.join(repo, 'web')).filter(f => f.endsWith('.js'))) {
  try { execFileSync(process.execPath, ['--check', f], { stdio: 'pipe' }); }
  catch (e) { problems.push(`${path.relative(repo, f)}: ${e.stderr.toString().split('\n').slice(0, 4).join(' ')}`); }
  // comments may mention the forbidden APIs; code may not
  const src = fs.readFileSync(f, 'utf8').split('\n').filter(l => !/^\s*(\/\/|\*|\/\*)/.test(l)).join('\n');
  for (const bad of ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'eval(']) {
    if (src.includes(bad)) problems.push(`${path.relative(repo, f)}: uses ${bad} (not allowed: CSP and XSS safety)`);
  }
}
for (const f of walk(path.join(repo, 'web')).filter(f => f.endsWith('.html'))) {
  const src = fs.readFileSync(f, 'utf8');
  if (/\son[a-z]+\s*=/.test(src) || /\sstyle\s*=/.test(src) || /<script>(?!\s*<\/script>)/.test(src))
    problems.push(`${path.relative(repo, f)}: inline script, style or handler (blocked by the CSP)`);
}

const report = fs.readFileSync(path.join(repo, 'src', 'Ferahla.Core', 'Report', 'report.html'), 'utf8');
const inline = report.split('<script>')[1]?.split('</script>')[0];
try { new Function(inline); } catch (e) { problems.push('report.html inline script: ' + e.message); }

if (problems.length) { console.log(problems.join('\n')); process.exit(1); }
console.log('check-web: ok');
