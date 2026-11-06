// stream-json fixture sanitizer: replaces personal/account data with fixed fake values.
// Usage: node sanitize-fixture.mjs <temp folder holding the raw output> <user profile path> <in> <out> [<in> <out> ...]
import { readFileSync, writeFileSync } from 'node:fs';

const [, , scratch, profile, ...pairs] = process.argv;
const maps = { req: new Map(), msg: new Map(), toolu: new Map(), uuid: new Map() };
const fake = (kind, real) => {
  const m = maps[kind];
  if (!m.has(real)) {
    const n = String(m.size + 1).padStart(4, '0');
    m.set(real, kind === 'uuid' ? `00000000-0000-4000-8000-00000000${n}` : `${kind}_fixture${n}`);
  }
  return m.get(real);
};
const FAKE_TIME = '2030-01-01T00:00:00.000Z';
const FAKE_UNIX = 1893456000;
const RATE_LIMIT = {
  status: 'allowed', resetsAt: FAKE_UNIX, rateLimitType: 'five_hour', utilization: 0.25,
  isUsingOverage: false, overageEnabled: false,
  unifiedWindows: {
    five_hour: { utilization: 0.25, resetsAt: FAKE_UNIX, status: 'allowed' },
    seven_day: { utilization: 0.25, resetsAt: FAKE_UNIX, status: 'allowed' },
  },
};
const esc = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const workRe = new RegExp(esc(scratch + '\\claude-work'), 'gi');
const scratchRe = new RegExp(esc(scratch), 'gi');
const profileRe = new RegExp(esc(profile), 'gi');
const userName = profile.split('\\').pop();
const slugRe = new RegExp('C--Users-' + esc(userName) + '-[A-Za-z0-9-]*claude-work', 'g');

const str = s => s
  .replace(workRe, 'C:\\Users\\user\\AppData\\Local\\Ferahla\\claude-work')
  .replace(scratchRe, 'C:\\Users\\user\\AppData\\Local\\Temp\\scratch')
  .replace(profileRe, 'C:\\Users\\user')
  .replace(slugRe, 'C--Users-user-AppData-Local-Ferahla-claude-work')
  .replace(/\\\\\.\\pipe\\LOCAL\\cc-msg-[0-9a-f]+/g, '\\\\.\\pipe\\LOCAL\\cc-msg-0000')
  // Real API ids are long (msg_011C…, 24+ chars); short ones (capability names like msg_lifecycle_v1) are left alone
  .replace(/\breq_[A-Za-z0-9]{16,}/g, r => fake('req', r))
  .replace(/\bmsg_[A-Za-z0-9]{16,}/g, r => fake('msg', r))
  .replace(/\btoolu_[A-Za-z0-9]{16,}/g, r => fake('toolu', r))
  .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, r => fake('uuid', r.toLowerCase()))
  .replace(/\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)/g, FAKE_TIME);

const walk = (v, key) => {
  if (typeof v === 'string') return str(v);
  if (Array.isArray(v)) return v.map(x => walk(x));
  if (v && typeof v === 'object') {
    const o = {};
    for (const [k, x] of Object.entries(v)) {
      if (k === 'rate_limit_info') o[k] = RATE_LIMIT;
      else if (k === 'credential_expires_at') o[k] = FAKE_TIME;
      else if (k === 'claude_code_version') o[k] = '0.0.0-fixture'; // the installed version fingerprints a machine
      else if (k === 'skills' || k === 'slash_commands') o[k] = ['example-skill'];
      else if (k === 'plugins') o[k] = [];
      else if (k === 'agents') o[k] = ['general-purpose'];
      else if (k === 'analytics_disabled' || k === 'product_feedback_disabled') o[k] = false;
      else if (/^(email|orgId|orgName|accountUuid|organizationUuid)$/i.test(k)) continue;
      // thinking signatures: encrypted blobs from the API; a fixed placeholder keeps the fixture fully synthetic
      else if (k === 'signature' && typeof x === 'string') o[k] = 'fixture-signature';
      else o[str(k)] = walk(x, k); // ids can also appear as object keys (wire_tool_inputs)
    }
    return o;
  }
  return v;
};

for (let i = 0; i < pairs.length; i += 2) {
  const lines = readFileSync(pairs[i], 'utf8').split(/\r?\n/).filter(Boolean);
  const out = lines.map(l => JSON.stringify(walk(JSON.parse(l))));
  writeFileSync(pairs[i + 1], out.join('\n') + '\n');
  console.log(`${pairs[i + 1]}: ${out.length} lines`);
}
