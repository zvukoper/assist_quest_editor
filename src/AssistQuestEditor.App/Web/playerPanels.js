(function () {
  "use strict";

  var CHARACTER_STAT_LABELS = {
    strength: "Сила",
    perception: "Восприятие",
    endurance: "Выносливость",
    charisma: "Харизма",
    intelligence: "Интеллект",
    agility: "Ловкость",
    luck: "Удача"
  };

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function resolveAssetUrl(path) {
    if (!path) return "";
    if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(path)) return path;
    return "../" + String(path).replace(/^\.?\//, "");
  }

  function reputationRows(snapshot) {
    var entries = Object.entries(snapshot?.reputation?.entries || {});
    var catalog = snapshot?.npcCatalog || [];

    return entries.map(function (pair) {
      var npcId = pair[0];
      var entry = pair[1] || {};
      var npc = catalog.find(function (item) {
        return String(item.id || "").toLowerCase() === String(npcId).toLowerCase();
      }) || {};

      return {
        npcId: npcId,
        name: npc.name || npcId,
        avatar: npc.avatar || "",
        value: Number(entry.value || 0),
        contacted: Boolean(entry.contacted)
      };
    }).filter(function (entry) {
      return entry.contacted;
    }).sort(function (a, b) {
      return b.value - a.value || a.name.localeCompare(b.name, "ru");
    });
  }

  function reputationView(snapshot, npcId, value) {
    var view = snapshot?.reputationViews?.[npcId];
    if (view) return view;

    return {
      value: value,
      valueLabel: value > 0 ? "+" + value : String(value),
      rangeName: "Нейтральный",
      rangeColor: "#c8ccd2",
      fillColor: "#8f9baa",
      progressPercent: Math.min(100, Math.abs(value) / 100),
      tooltip: "Репутация: " + value
    };
  }

  function characterMarkup(snapshot) {
    var character = snapshot?.character || {};
    var stats = Object.entries(character.stats || {});
    var skills = character.skills || [];

    return [
      "<div class='characterBody'>",
      "<div class='characterSectionTitle'>Статы</div>",
      "<div class='characterStats'>",
      stats.map(function (pair) {
        var key = pair[0], value = pair[1];
        return "<div class='characterStat' data-game-tooltip='" +
          escapeHtml((CHARACTER_STAT_LABELS[key] || key) + ": " + Number(value) + " из 10") + "'>" +
          "<span class='characterStatName'>" + escapeHtml(CHARACTER_STAT_LABELS[key] || key) + "</span>" +
          "<span class='characterStatValue'>" + Number(value) + "</span>" +
        "</div>";
      }).join(""),
      "</div>",
      "<div class='characterSectionTitle' style='margin-top:12px'>Скиллы</div>",
      "<div class='characterSkills'>",
      skills.map(function (skill) {
        return "<div class='characterSkill' data-game-tooltip='" +
          escapeHtml(skill.description || skill.name) + "'>" +
          "<span class='characterSkillName'>" + escapeHtml(skill.name) + "</span>" +
          "<span class='characterSkillLevel'>" +
            escapeHtml(skill.kind === "Levelled"
              ? ("Уровень " + Number(skill.level || 0) + "/" + Number(skill.maxLevel || 100))
              : (skill.unlocked ? "Получен" : "Не изучен")) +
          "</span>" +
          "<div class='characterSkillDesc'>" + escapeHtml(skill.description || "") + "</div>" +
        "</div>";
      }).join(""),
      "</div>",
      "</div>"
    ].join("");
  }

  function reputationMarkup(snapshot) {
    var rows = reputationRows(snapshot);

    if (!rows.length) {
      return "<div class='reputationEmpty'>" +
        "<div class='gamePanelSub'>Пока нет НПЦ, с которыми состоялся контакт.</div>" +
        "<div class='notice' style='margin-top:8px'>Репутация появляется после первой реплики или выбора в диалоге.</div>" +
      "</div>";
    }

    return "<div class='reputationList'>" +
      rows.map(function (row) {
        var view = reputationView(snapshot, row.npcId, row.value);
        return "<article class='reputationRow'>" +
          "<img class='reputationAvatar' src='" + escapeHtml(resolveAssetUrl(row.avatar)) +
            "' alt='' loading='lazy' onerror=\"this.removeAttribute('src');this.className+=' missing'\">" +
          "<div class='reputationBody'>" +
            "<div class='reputationTop'>" +
              "<span class='reputationName'>" + escapeHtml(row.name) + "</span>" +
              "<span class='reputationValue'>" + escapeHtml(view.valueLabel) + "</span>" +
            "</div>" +
            "<div class='reputationRange' style='color:" + escapeHtml(view.rangeColor) + "'>" +
              escapeHtml(view.rangeName) +
            "</div>" +
            "<div class='reputationTrack' data-game-tooltip='" + escapeHtml(view.tooltip || row.name) + "'>" +
              "<div class='reputationFill' style=\"width:" + view.progressPercent +
                "%;background:" + escapeHtml(view.fillColor) + "\"></div>" +
            "</div>" +
          "</div>" +
        "</article>";
      }).join("") +
    "</div>";
  }

  function renderTabs(container, snapshot, activeTab, onTabChanged) {
    if (!container || !snapshot) return;

    var tabs = [
      ["character", "Персонаж"],
      ["reputation", "Репутация"]
    ];

    container.innerHTML =
      "<div class='gamePanelTabs playerWindowTabs' role='tablist'>" +
        tabs.map(function (tab) {
          return "<button class='gamePanelTab" +
            (activeTab === tab[0] ? " active" : "") +
            "' type='button' role='tab' data-player-tab='" + tab[0] +
            "' aria-selected='" + (activeTab === tab[0] ? "true" : "false") + "'>" +
            escapeHtml(tab[1]) +
          "</button>";
        }).join("") +
      "</div>" +
      "<div class='gamePanelTabBody playerWindowTabBody'>" +
        (activeTab === "reputation"
          ? reputationMarkup(snapshot)
          : characterMarkup(snapshot)) +
      "</div>";

    container.querySelectorAll("[data-player-tab]").forEach(function (button) {
      button.addEventListener("click", function () {
        var next = button.dataset.playerTab;
        if (!next || next === activeTab) return;
        onTabChanged(next);
      });
    });
  }

  window.AssistPlayerPanels = {
    renderTabs: renderTabs,
    characterMarkup: characterMarkup,
    reputationMarkup: reputationMarkup
  };
})();
