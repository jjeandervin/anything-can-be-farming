import { createHash } from 'node:crypto';
import { readFileSync, existsSync, writeFileSync } from 'node:fs';
import { spawn, spawnSync } from 'node:child_process';

// The nested volume hides host dependencies and survives container recreation.
const fingerprint = createHash('sha256')
  .update(readFileSync('package-lock.json'))
  .update(readFileSync('package.json'))
  .update(process.version + process.platform + process.arch)
  .digest('hex');
const marker = 'node_modules/.acbf-dependencies';
if (!existsSync(marker) || readFileSync(marker, 'utf8') !== fingerprint) {
  const install = spawnSync('npm', ['ci', '--no-audit', '--no-fund'], { stdio: 'inherit' });
  if (install.status !== 0) process.exit(install.status || 1);
  writeFileSync(marker, fingerprint);
}
await import('./config.mjs');
const server = spawn(process.execPath, [
  'node_modules/@angular/cli/bin/ng.js', 'serve',
  '--host', '0.0.0.0', '--port', '4200', '--poll', '1000',
], { stdio: 'inherit' });
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => server.kill(signal));
}
server.on('exit', (code) => process.exit(code ?? 0));
