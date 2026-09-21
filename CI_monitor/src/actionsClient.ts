/**
 * Загрузка страницы GitHub Actions.
 *
 * Только чтение публичной HTML-страницы: без API, без токена, без авторизации.
 * ETag из ответа используется на следующем опросе — при 304 тело не передаётся,
 * что экономит трафик и не расходует лимит запросов GitHub API.
 */

import { CiRun, ParsedActionsPage } from './types';
import { findLatestRun, parseActionsPage } from './parser';

/** Ошибка загрузки с признаком «повторять бессмысленно» (404, 401, 403). */
export class FetchError extends Error {
  constructor(message: string, readonly fatal: boolean) {
    super(message);
    this.name = 'FetchError';
  }
}

/** Результат одного опроса. */
export interface PollResult {
  run: CiRun | null;
  page: ParsedActionsPage;
  /** Ответ получен из кэша (304 Not Modified). */
  notModified: boolean;
}

/** Универсальный клиент с ETag-кэшем для одной страницы. */
export class ActionsClient {
  private etag: string | null = null;

  /** Сброс кэша (нужен при смене репозитория или ветки). */
  reset(): void {
    this.etag = null;
  }

  /**
   * Загрузка и разбор страницы.
   * @param pageUrl URL страницы Actions;
   * @param owner владелец репозитория;
   * @param repo имя репозитория;
   * @param branch целевая ветка;
   * @param ifNoneMatchEtag ETag предыдущего ответа (для 304).
   */
  async fetchPage(
    pageUrl: string,
    owner: string,
    repo: string,
    branch: string | null,
    timeoutMs: number
  ): Promise<{ html: string | null; notModified: boolean }> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);

    try {
      const headers: Record<string, string> = {
        Accept: 'text/html,application/xhtml+xml',
        'User-Agent': 'ci-monitor-vscode-extension',
      };
      if (this.etag) headers['If-None-Match'] = this.etag;

      const response = await fetch(pageUrl, {
        headers,
        signal: controller.signal,
        redirect: 'follow',
      });

      if (response.status === 304) return { html: null, notModified: true };

      if (response.status === 404) {
        throw new FetchError(
          `Репозиторий или страница Actions не найдены (HTTP 404): ${owner}/${repo}`,
          true
        );
      }
      if (response.status === 401 || response.status === 403) {
        // Для публичного репозитория это чаще всего лимит обращений GitHub.
        throw new FetchError(
          `GitHub отклонил запрос (HTTP ${response.status}). Возможен лимит обращений — увеличьте ciMonitor.refreshInterval.`,
          true
        );
      }
      if (!response.ok) {
        throw new FetchError(`GitHub вернул HTTP ${response.status} для ${pageUrl}`, false);
      }

      const etag = response.headers.get('etag');
      if (etag) this.etag = etag;

      const html = await response.text();
      if (!html.includes('actions/runs/')) {
        throw new FetchError(
          'Страница Actions получена, но список проверок в ней отсутствует (изменилась разметка GitHub).',
          false
        );
      }

      return { html, notModified: false };
    } catch (error) {
      if (error instanceof FetchError) throw error;
      if (error instanceof Error && error.name === 'AbortError') {
        throw new FetchError(`Таймаут запроса (${timeoutMs} мс): ${pageUrl}`, false);
      }
      throw new FetchError(
        `Не удалось загрузить страницу Actions: ${error instanceof Error ? error.message : String(error)}`,
        false
      );
    } finally {
      clearTimeout(timer);
    }
  }

  /**
   * Полный опрос: загрузка + разбор + выбор последней проверки.
   * @param cache последний успешный разбор (используется при ответе 304).
   */
  async poll(
    pageUrl: string,
    owner: string,
    repo: string,
    branch: string | null,
    timeoutMs: number,
    cache: ParsedActionsPage | null
  ): Promise<PollResult> {
    const { html, notModified } = await this.fetchPage(pageUrl, owner, repo, branch, timeoutMs);

    if (notModified) {
      if (!cache) {
        // Кэш потерян (первый запрос после смены цели) — запрашиваем тело заново.
        this.reset();
        const retry = await this.fetchPage(pageUrl, owner, repo, branch, timeoutMs);
        const page = retry.html ? parseActionsPage(retry.html, owner, repo) : { runs: [], branches: [] };
        return { run: findLatestRun(page.runs, branch), page, notModified: false };
      }
      return { run: findLatestRun(cache.runs, branch), page: cache, notModified: true };
    }

    const page = parseActionsPage(html ?? '', owner, repo);
    if (page.runs.length === 0) {
      throw new FetchError(
        'В разметке страницы Actions не найдено ни одной проверки (возможно, изменён формат GitHub).',
        false
      );
    }
    return { run: findLatestRun(page.runs, branch), page, notModified: false };
  }
}
