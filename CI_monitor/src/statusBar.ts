/**
 * Плашка статуса в строке состояния.
 *
 * Размещение: правая часть строки состояния, приоритет 99 — ровно рядом с
 * Ollama Usage Monitor, у которого приоритет 100. Панель состояния сортирует
 * элементы справа налево по убыванию приоритета, поэтому #99 встаёт
 * непосредственно слева от плашки Ollama.
 */

import * as vscode from 'vscode';
import { BadgePresentation } from './badge';

export class CiStatusBar {
  private readonly item: vscode.StatusBarItem;

  constructor(openCommand: string) {
    this.item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 99);
    this.item.name = 'CI Monitor';
    this.item.command = openCommand;
    this.item.text = '$(sync~spin) CI';
    this.item.tooltip = 'CI Monitor: загрузка статуса проверок…';
    this.item.color = '#ffa500';
    this.item.show();
  }

  update(presentation: BadgePresentation): void {
    this.item.text = presentation.text;
    this.item.tooltip = presentation.tooltip;
    this.item.accessibilityInformation = { label: presentation.accessibleText };

    switch (presentation.backgroundKind) {
      case 'error':
        this.item.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
        // Тема сама подбирает контрастный цвет текста при заданном фоне.
        this.item.color = undefined;
        break;
      case 'warning':
        this.item.backgroundColor = new vscode.ThemeColor('statusBarItem.warningBackground');
        this.item.color = undefined;
        break;
      default:
        this.item.backgroundColor = undefined;
        this.item.color = presentation.color;
        break;
    }

    if (presentation.visible) this.item.show();
    else this.item.hide();
  }

  dispose(): void {
    this.item.dispose();
  }
}
