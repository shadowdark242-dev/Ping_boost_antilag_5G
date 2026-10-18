using PingBoost;

namespace PingBoost.Services;

public static class RecommendationService
{
    public static IReadOnlyList<string> GetInitialRecommendations() =>
    [
        "Сравните несколько целей: шлюз оператора, стабильный публичный адрес и игровой сервер.",
        "Повторите замер в разное время и сохраните исходные результаты перед изменением настроек.",
        "Traceroute и ICMP — ориентировочные тесты: промежуточные узлы могут не отвечать на ping."
    ];

    public static IReadOnlyList<string> Create(
        IReadOnlyList<(string Host, TargetStatistics Statistics)> results)
    {
        var recommendations = new List<string>();
        if (results.Count == 0)
            return GetInitialRecommendations();

        foreach (var (host, stats) in results)
        {
            if (stats.TotalSamples < 5)
            {
                recommendations.Add($"{host}: накопите хотя бы 5 измерений для более показательной оценки.");
                continue;
            }

            if (stats.LossPercent >= 5)
                recommendations.Add($"{host}: заметны пропуски ICMP-ответов ({stats.LossPercent:0.#}%). Проверьте сигнал и сравните результат с другой целью; узел может ограничивать ICMP.");
            else if (stats.LossPercent > 0)
                recommendations.Add($"{host}: есть отдельные пропуски ICMP-ответов. Повторите тест позже, прежде чем менять настройки.");

            if (stats.JitterMilliseconds is >= 20)
                recommendations.Add($"{host}: задержка заметно колеблется. Сравните Wi‑Fi и мобильное подключение и повторите тест без фоновых загрузок.");

            if (stats.AverageMilliseconds is >= 100)
                recommendations.Add($"{host}: средняя задержка высокая для этой цели. Сравните маршрут до других узлов; приложение не меняет маршрут автоматически.");
        }

        if (recommendations.Count == 0)
            recommendations.Add("По текущим ICMP-замерам выраженной нестабильности не видно. Это не гарантирует такое же качество UDP-соединения с игрой.");

        recommendations.Add("Активный bufferbloat-тест пока не запускается: он создавал бы дополнительную нагрузку на канал.");
        return recommendations.Distinct(StringComparer.CurrentCulture).ToArray();
    }
}
