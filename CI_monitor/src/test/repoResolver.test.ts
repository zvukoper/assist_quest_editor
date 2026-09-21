/**
 * Проверки определения репозитория, ветки и адреса страницы Actions.
 */

import { strict as assert } from 'node:assert';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { actionsPageUrl, findGitRoot, parseGitHubRemote, parseRepoSetting } from '../repoResolver';

test('HTTPS-адрес GitHub разбирается корректно', () => {
  assert.deepEqual(parseGitHubRemote('https://github.com/zvukoper/assist_quest_editor.git'), {
    owner: 'zvukoper',
    repo: 'assist_quest_editor',
  });
  assert.deepEqual(parseGitHubRemote('https://github.com/zvukoper/assist_quest_editor'), {
    owner: 'zvukoper',
    repo: 'assist_quest_editor',
  });
});

test('SSH-адрес GitHub разбирается корректно', () => {
  assert.deepEqual(parseGitHubRemote('git@github.com:zvukoper/assist_quest_editor.git'), {
    owner: 'zvukoper',
    repo: 'assist_quest_editor',
  });
  assert.deepEqual(parseGitHubRemote('ssh://git@github.com/zvukoper/assist_quest_editor.git'), {
    owner: 'zvukoper',
    repo: 'assist_quest_editor',
  });
});

test('токен в HTTPS-адресе не мешает разбору', () => {
  assert.deepEqual(parseGitHubRemote('https://x-access-token:abc123@github.com/zvukoper/assist_quest_editor.git'), {
    owner: 'zvukoper',
    repo: 'assist_quest_editor',
  });
});

test('не-GitHub адреса игнорируются', () => {
  assert.equal(parseGitHubRemote('https://gitlab.com/zvukoper/assist_quest_editor.git'), null);
  assert.equal(parseGitHubRemote(''), null);
  assert.equal(parseGitHubRemote('просто текст'), null);
});

test('настройка репозитория: "владелец/имя"', () => {
  assert.deepEqual(parseRepoSetting('zvukoper/assist_quest_editor'), {
    owner: 'zvukoper',
    repo: 'assist_quest_editor',
  });
  assert.deepEqual(parseRepoSetting(' zvukoper/assist_quest_editor.git '), {
    owner: 'zvukoper',
    repo: 'assist_quest_editor',
  });
  assert.equal(parseRepoSetting(''), null);
  assert.equal(parseRepoSetting('без-слэша'), null);
});

test('URL страницы Actions без ветки', () => {
  assert.equal(
    actionsPageUrl({ owner: 'zvukoper', repo: 'assist_quest_editor' }, null),
    'https://github.com/zvukoper/assist_quest_editor/actions'
  );
});

test('URL страницы Actions с фильтром по ветке', () => {
  assert.equal(
    actionsPageUrl({ owner: 'zvukoper', repo: 'assist_quest_editor' }, 'main'),
    'https://github.com/zvukoper/assist_quest_editor/actions?query=branch%3Amain'
  );
});

test('имя ветки с косой чертой кодируется в URL', () => {
  const url = actionsPageUrl({ owner: 'zvukoper', repo: 'assist_quest_editor' }, 'feature/new-ui');
  assert.ok(url.includes('branch%3Afeature%2Fnew-ui'), url);
});

test('поиск git-корня останавливается на файловой системе верхнего уровня', () => {
  // Каталог вне репозитория: функция обязана вернуть null, а не зациклиться.
  const tempDir = os.tmpdir();
  const found = findGitRoot(path.join(tempDir, '__ci_monitor_no_git__'));
  assert.equal(found, null);
});

test('поиск git-корня находит корень текущего проекта', () => {
  // Сам репозиторий CI_monitor находится внутри git-проекта, поэтому корень должен быть найден.
  const found = findGitRoot(__dirname);
  assert.ok(found, 'git-корень не найден');
  assert.ok(found.length > 0);
});
