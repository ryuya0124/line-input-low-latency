import { spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
if (process.platform !== 'win32') throw new Error('Windows is required.');
const root = path.dirname(fileURLToPath(import.meta.url));
const build = path.join(root, 'build');
mkdirSync(build, { recursive: true });
const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const builder = path.join(build, 'BuildTool.exe');
function run(file, args) {
  const result = spawnSync(file, args, { cwd: root, stdio: 'inherit', shell: false });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status || 1);
}
run(compiler, ['/nologo', '/target:exe', '/platform:x64', '/optimize+', '/codepage:65001', `/out:${builder}`, '/reference:System.Drawing.dll', '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll', path.join(root, 'src', 'BuildTool.cs'), path.join(root, 'src', 'IconBuilder.cs')]);
run(builder, [root]);
