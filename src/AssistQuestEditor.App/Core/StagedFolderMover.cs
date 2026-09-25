using AssistQuestEditor.Domain;

namespace AssistQuestEditor.App;

/// <summary>
/// Ставит ПОДГОТОВЛЕННУЮ папку на её постоянное место.
///
/// Импорт (мира, кампании) распаковывает ресурс во временную папку и затем
/// переносит её на место. Перенос делался одним <c>Directory.Move</c>, и это
/// падало на обычной машине: временный каталог лежит на системном диске, а
/// пользовательские данные — в Документах, которые часто перенесены на другой
/// диск. Ошибка была не «иногда», а всегда:
/// «Source and destination path must have identical roots».
///
/// Поэтому решает <see cref="StagedFolderRules.CanRenameIntoPlace"/>: внутри
/// тома — переименование (мгновенно и атомарно), между томами — копирование.
///
/// Копирование идёт в промежуточную папку ТОГО ЖЕ тома, что и место установки,
/// и лишь последним шагом переименовывается на место. Это сохраняет главное
/// свойство прежнего приёма: каталог миров никогда не видит частично
/// скопированный ресурс. Файл-определитель копируется последним — иначе стор
/// «увидел» бы мир или кампанию раньше их содержимого.
/// </summary>
public static class StagedFolderMover
{
    /// <summary>Признак незавершённой установки: имя промежуточной папки.</summary>
    private const string PartialSuffix = ".installing";

    /// <summary>
    /// Переносит <paramref name="staging"/> в <paramref name="targetFolder"/>.
    ///
    /// Папка назначения должна быть СВОБОДНА: удаление существующего ресурса
    /// остаётся решением вызывающего кода, который знает, что именно он
    /// перезаписывает по явному согласию пользователя. Молчаливое удаление здесь
    /// было бы худшим видом «помощи».
    ///
    /// <paramref name="definingFileName"/> — файл, по которому стор узнаёт ресурс
    /// (<c>world.aqworld</c>, <c>campaign.aqcampaign</c>). При копировании он
    /// переносится последним.
    /// </summary>
    public static void IntoPlace(string staging, string targetFolder, string definingFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staging);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(definingFileName);

        if (!Directory.Exists(staging))
            throw new DirectoryNotFoundException("Нет подготовленной папки для установки: " + staging);

        if (Directory.Exists(targetFolder) || File.Exists(targetFolder))
            throw new InvalidOperationException("Папка назначения занята: " + targetFolder);

        var parent = Path.GetDirectoryName(Path.GetFullPath(targetFolder));
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);

        if (StagedFolderRules.CanRenameIntoPlace(staging, targetFolder))
        {
            Directory.Move(staging, targetFolder);
            AppLogger.Info("StagedFolderMover: папка поставлена переименованием.",
                $"staging={staging}; target={targetFolder}");
            return;
        }

        // Между томами переименование невозможно. Копия кладётся рядом с местом
        // установки: последнее действие — переименование внутри одного тома,
        // поэтому частично скопированный ресурс не появляется в каталоге миров.
        var partial = targetFolder + PartialSuffix;

        try
        {
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);

            CopyTree(staging, partial, definingFileName);
            Directory.Move(partial, targetFolder);

            AppLogger.Info("StagedFolderMover: папка поставлена копированием между томами.",
                $"staging={staging}; target={targetFolder}; partial={partial}");
        }
        catch
        {
            try
            {
                if (Directory.Exists(partial))
                    Directory.Delete(partial, recursive: true);
            }
            catch (Exception cleanupError)
            {
                // Уборка не должна скрывать исходную ошибку: о неудаче уборки
                // достаточно записи в журнале.
                AppLogger.Warn("StagedFolderMover: не удалось убрать промежуточную папку.",
                    partial + "; " + cleanupError.Message);
            }

            throw;
        }
        finally
        {
            // Подготовительная папка больше не нужна: содержимое уже перенесено.
            // При переименовании её нет — удалять нечего.
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception cleanupError)
                {
                    AppLogger.Warn("StagedFolderMover: не удалось убрать подготовительную папку.",
                        staging + "; " + cleanupError.Message);
                }
            }
        }
    }

    private static void CopyTree(string source, string target, string definingFileName)
    {
        Directory.CreateDirectory(target);

        // Пустые каталоги копируются тоже: в мире они несут смысл (куда класть
        // сцены и сохранения), и «импорт прошёл, а структуры нет» — поломка,
        // заметная автору только после начала работы.
        foreach (var folder in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, folder);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        var files = Directory
            .EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path))
            .ToArray();

        foreach (var relative in StagedFolderRules.OrderForCopy(files, definingFileName))
        {
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination, overwrite: true);
        }
    }
}
