/**
 * Определение репозитория и ветки (БЕЗ `vscode` — командная строка git).
 *
 * Приоритет:
 *   1. настройка `ciMonitor.repository` / `ciMonitor.branch`;
 *   2. git remote текущего открытого проекта;
 *   3. запасной путь — чтение `.git/config` без вызова git.
 */

import { execFile } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { RepoRef } from './types';

/** Разбор URL git remote в пару «владелец/репозиторий». */
export function parseGitHubRemote(url: string): RepoRef | null {
  const value = url.trim();
  if (!value) return null;

  // git@github.com:owner/repo.git, ssh://git@github.com/owner/repo.git,
  // https://github.com/owner/repo.git
  const match =
    /^git@github\.com:([^/]+)\/(.+?)(?:\.git)?$/i.exec(value) ??
    /^ssh:\/\/[^/]*github\.com\/([^/]+)\/(.+?)(?:\.git)?$/i.exec(value) ??
    /^https?:\/\/(?:[^@/]*@)?github\.com\/([^/]+)\/(.+?)(?:\.git)?\/?$/i.exec(value);

  if (!match) return null;
  const owner = match[1].trim();
  const repo = match[2].trim().replace(/\/+$/, '');
  if (!owner || !repo) return null;
  return { owner, repo };
}

/** Значение `owner/repo` из настройки. */
export function parseRepoSetting(value: string): RepoRef | null {
  const match = /^\s*([^/\s]+)\/([^\s]+)\s*$/.exec(value ?? '');
  if (!match) return null;
  return { owner: match[1], repo: match[2].replace(/\.git$/i, '') };
}

/** Запуск git в указанном каталоге. Возвращает `null` при любой ошибке. */
export function runGit(cwd: string, args: string[]): Promise<string | null> {
  return new Promise((resolve) => {
    execFile('git', args, { cwd, windowsHide: true, timeout: 5000 }, (error, stdout) => {
      if (error) return resolve(null);
      resolve(stdout.trim());
    });
  });
}

/** Поиск корня репозитория вверх по дереву каталогов. */
export function findGitRoot(startDir: string): string | null {
  let dir = path.resolve(startDir);
  for (;;) {
    if (fs.existsSync(path.join(dir, '.git'))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

/** Запасной разбор `.git/config` без вызова git. */
export function readRemoteFromGitConfig(gitRoot: string): RepoRef | null {
  try {
    const config = fs.readFileSync(path.join(gitRoot, '.git', 'config'), 'utf8');
    const section = /\[remote "origin"\]([\s\S]*?)(?=\n\[|$)/.exec(config);
    if (!section) return null;
    const urlLine = /^\s*url\s*=\s*(.+)$/m.exec(section[1]);
    if (!urlLine) return null;
    return parseGitHubRemote(urlLine[1]);
  } catch {
    return null;
  }
}

/** Репозиторий из git: `origin`, иначе первый попавшийся remote. */
export async function resolveRepoFromGit(startDir: string): Promise<RepoRef | null> {
  const gitRoot = findGitRoot(startDir);
  if (!gitRoot) return null;

  const origin = await runGit(gitRoot, ['remote', 'get-url', 'origin']);
  const fromOrigin = origin ? parseGitHubRemote(origin) : null;
  if (fromOrigin) return fromOrigin;

  const remotes = await runGit(gitRoot, ['remote']);
  for (const name of (remotes ?? '').split('\n').map((r) => r.trim()).filter(Boolean)) {
    const url = await runGit(gitRoot, ['remote', 'get-url', name]);
    const parsed = url ? parseGitHubRemote(url) : null;
    if (parsed) return parsed;
  }

  return readRemoteFromGitConfig(gitRoot);
}

/** Текущая ветка git. `null` — определить не удалось (detached HEAD, нет git). */
export async function resolveBranchFromGit(startDir: string): Promise<string | null> {
  const gitRoot = findGitRoot(startDir);
  if (!gitRoot) return null;

  const branch = await runGit(gitRoot, ['rev-parse', '--abbrev-ref', 'HEAD']);
  if (!branch || branch === 'HEAD') {
    return runGit(gitRoot, ['symbolic-ref', '--short', 'refs/remotes/origin/HEAD']);
  }
  return branch;
}

/** URL страницы списка проверок. Ветку фильтруем через query, чтобы не терять все запуски. */
export function actionsPageUrl(ref: RepoRef, branch: string | null): string {
  const base = `https://github.com/${ref.owner}/${ref.repo}/actions`;
  return branch ? `${base}?query=branch%3A${encodeURIComponent(branch)}` : base;
}
