import { readdirSync } from 'node:fs';
import { join } from 'node:path';

export function currentInstall(repository) {
    const migrations = join(repository, 'Migrations');
    const folders = readdirSync(migrations, { withFileTypes: true }).filter(entry => entry.isDirectory() && entry.name.endsWith('-Current'));
    if (folders.length !== 1) throw new Error(`Expected one Current installation under ${migrations}.`);
    return join(migrations, folders[0].name);
}
