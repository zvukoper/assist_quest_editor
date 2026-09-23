using AssistQuestEditor.Domain;
using Xunit;

namespace AssistQuestEditor.Domain.Tests;

/// <summary>
/// Правила ручной правки списка перекрёстков: что считать исключённым, что
/// добавленным и кто из них важнее.
///
/// Проверяется именно ПРАВИЛО, а не запись файла: правило решает, попадёт ли узел
/// в поиск, и ошибка здесь тихая — узел просто не находится, без сообщений.
/// </summary>
public sealed class JunctionReviewTests
{
    private static JunctionPoint P(double x, double z = 0) => new(x, z);

    /// <summary>Расстояние между узлами — для проверок «узел на месте».</summary>
    private static double Distance(JunctionPoint a, JunctionPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    [Fact]
    public void ExcludedJunctionsFallOutOfTheList()
    {
        var review = new JunctionReview(new[] { P(100) }, Array.Empty<JunctionPoint>());
        var detected = new[] { P(0), P(100), P(200) };

        var result = review.Apply(detected);

        Assert.Equal(new[] { 0d, 200d }, result.Select(point => point.X));
    }

    [Fact]
    public void AddedJunctionsAreAppendedAndNotDuplicated()
    {
        var review = new JunctionReview(Array.Empty<JunctionPoint>(), new[] { P(300), P(0) });
        var detected = new[] { P(0), P(100) };

        var result = review.Apply(detected);

        // 300 дописан, 0 не продублирован: автор добавил то, что поиск уже нашёл.
        Assert.Equal(new[] { 0d, 100d, 300d }, result.Select(point => point.X));
    }

    [Fact]
    public void AddedJunctionAlwaysSurvivesAnyExclusionNearby()
    {
        // Забота автора: вычищенное кольцо (десятки серых точек) не должно
        // «обнулить» ОДИН зелёный узел, поставленный поверх.
        //
        // Гарантия обеспечивается ПОСТРОЕНИЕМ: добавленные узлы дописываются
        // отдельным проходом и в вычитании не участвуют вовсе. Проверяется именно
        // это — при любом окружении серых точек зелёный узел на месте.
        var ring = new[]
        {
            P(1000, 1000), P(1010, 1000), P(1020, 1000), P(1030, 1000),
            P(1004, 1000)  // автоматика нашла узел почти там же, где автор поставил свой
        };

        var manual = P(1004, 1000);
        var review = new JunctionReview(ring, new[] { manual });

        // Автоматика нашла узел вплотную к вычищенным — и всё равно он есть.
        var result = review.Apply(new[] { P(0), P(1004, 1000), P(2000) });
        Assert.Contains(result, point => Distance(point, manual) <= JunctionReview.MatchRadius);

        // И даже когда автоматика в этом месте не нашла НИЧЕГО.
        var onlyManual = review.Apply(new[] { P(0), P(2000) });
        Assert.Contains(onlyManual, point => Distance(point, manual) <= JunctionReview.MatchRadius);

        // И когда исключено ВСЁ вокруг на десятки метров.
        var denseRing = Enumerable.Range(0, 40)
            .Select(i => new JunctionPoint(1000 + i * 2, 1000))
            .ToArray();
        var manualNearRing = P(1004, 1000);
        var denseReview = new JunctionReview(denseRing, new[] { manualNearRing });
        var denseResult = denseReview.Apply(denseRing);

        Assert.Contains(denseResult, point => Distance(point, manualNearRing) <= JunctionReview.MatchRadius);
    }

    [Fact]
    public void ConfirmedJunctionKeepsItsPlaceInsteadOfMovingToTheEnd()
    {
        // Защищает ТО ЖЕ, что и AddedJunctionSurvivesEvenWhenNothingIsDetectedThere,
        // но с другой стороны: узел не только остаётся, но и не переезжает в конец.
        var place = P(200);
        var review = new JunctionReview(new[] { P(200.2) }, new[] { place });

        var result = review.Apply(new[] { P(100), P(200), P(300) });

        Assert.Equal(new[] { 100d, 200d, 300d }, result.Select(point => point.X));
    }

    [Fact]
    public void AddedJunctionSurvivesEvenWhenNothingIsDetectedThere()
    {
        // Второй случай того же сценария: автоматика в этом месте НИЧЕГО не нашла
        // (её узлы вычищены), и добавленный узел приходит только из правки.
        var review = new JunctionReview(new[] { P(1000), P(1010) }, new[] { P(1005) });

        var result = review.Apply(Array.Empty<JunctionPoint>());

        Assert.Single(result);
        Assert.Equal(1005d, result[0].X);
    }

    [Fact]
    public void ExclusionIsAboutAutomationNotAboutThePlace()
    {
        // «Исключить» означает «не верю автоматике здесь», а не «запрещаю этому
        // месту быть перекрёстком». Проверяются оба направления:
        var place = P(500, 700);

        // 1. Исключён ровно тот узел, который нашла автоматика — он уходит.
        var review = new JunctionReview(new[] { place }, Array.Empty<JunctionPoint>());
        Assert.Empty(review.Apply(new[] { place }));

        // 2. То же место, но автор добавил здесь СВОЙ узел (та же координата) —
        //    он остаётся: подтверждение вручную отменяет «не верю автоматике».
        var confirmed = new JunctionReview(new[] { place }, new[] { place });
        var confirmedResult = confirmed.Apply(new[] { place });
        Assert.Single(confirmedResult);
        Assert.Equal(place, confirmedResult[0]);

        // 3. И добавленный узел переживает исключение ДАЖЕ когда автоматика в этом
        //    месте ничего не нашла (все её узлы вычищены ранее).
        var manualOnly = new JunctionReview(new[] { P(500, 700) }, new[] { P(500, 700) });
        Assert.Single(manualOnly.Apply(Array.Empty<JunctionPoint>()));
    }

    [Fact]
    public void MatchingUsesToleranceNotExactCoordinates()
    {
        // Координаты округляются при записи, а список может быть пересчитан с
        // другой точностью: правка обязана найти свой узел с допуском.
        //
        // Все расстояния здесь РАЗНЫЕ по X и по Z, чтобы проверка не прошла по
        // случайному совпадению одной оси: считаем расстояние целиком.
        var review = new JunctionReview(new[] { new JunctionPoint(100, 200) }, Array.Empty<JunctionPoint>());

        // 0.3 м — внутри допуска (0.5): узел исключён.
        Assert.Empty(review.Apply(new[] { new JunctionPoint(100.3, 200) }));

        // 0.48 м (0.3 + 0.375) — ещё внутри допуска.
        Assert.Empty(review.Apply(new[] { new JunctionPoint(100.3, 200.375) }));

        // 0.6 м по X — уже снаружи: это другой перекрёсток.
        Assert.Single(review.Apply(new[] { new JunctionPoint(100.6, 200) }));

        // 0.5 м ровно: на границе, включительно — исключён.
        Assert.Empty(review.Apply(new[] { new JunctionPoint(100.5, 200) }));

        // То же расстояние по другой оси: допуск не должен зависеть от оси.
        Assert.Empty(review.Apply(new[] { new JunctionPoint(100, 200.5) }));
        Assert.Single(review.Apply(new[] { new JunctionPoint(100, 201) }));
    }

    [Fact]
    public void OrderIsPreservedForNavigation()
    {
        // Порядок важен для кнопок «следующий/предыдущий»: автор видит тот же
        // список, что и поиск, а добавленные идут в конец.
        var review = new JunctionReview(new[] { P(100) }, new[] { P(50) });
        var detected = new[] { P(300), P(200), P(100), P(400) };

        var result = review.Apply(detected);

        Assert.Equal(new[] { 300d, 200d, 400d, 50d }, result.Select(point => point.X));
    }
}
