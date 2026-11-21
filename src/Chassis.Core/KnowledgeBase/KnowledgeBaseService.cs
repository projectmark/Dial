using Chassis.Core.Localization;

namespace Chassis.Core.KnowledgeBase;

public sealed record KnowledgeArticle(
    string Id,
    string CategoryKey,
    string TitleKey,
    string SummaryKey,
    string BodyKey,
    string? BadgeText = null,
    string? CopyableReference = null,
    int Order = 0);

/// <summary>
/// Registry and localized search engine for built-in Help and Knowledge Base documentation articles.
/// </summary>
public interface IKnowledgeBaseService
{
    void RegisterArticle(KnowledgeArticle article);
    IReadOnlyList<KnowledgeArticle> GetAllArticles();
    IReadOnlyList<string> GetCategories();
    IReadOnlyList<KnowledgeArticle> Search(string? query, string? categoryKey, ILocalizationService localization);
}

/// <summary>
/// Thread-safe in-memory knowledge base registry populated by the host and mounted modules.
/// </summary>
public sealed class KnowledgeBaseService : IKnowledgeBaseService
{
    private readonly List<KnowledgeArticle> _articles = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void RegisterArticle(KnowledgeArticle article)
    {
        ArgumentNullException.ThrowIfNull(article);
        lock (_gate)
        {
            _articles.RemoveAll(a => string.Equals(a.Id, article.Id, StringComparison.OrdinalIgnoreCase));
            _articles.Add(article);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KnowledgeArticle> GetAllArticles()
    {
        lock (_gate)
        {
            return _articles.OrderBy(a => a.Order).ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCategories()
    {
        lock (_gate)
        {
            return _articles
                .Select(a => a.CategoryKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KnowledgeArticle> Search(string? query, string? categoryKey, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        var q = query?.Trim();

        return GetAllArticles()
            .Where(article =>
            {
                if (!string.IsNullOrWhiteSpace(categoryKey) &&
                    !string.Equals(categoryKey, "All", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(article.CategoryKey, categoryKey, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(q))
                {
                    return true;
                }

                return localization[article.TitleKey].Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       localization[article.SummaryKey].Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       localization[article.BodyKey].Contains(q, StringComparison.OrdinalIgnoreCase) ||
                       (article.CopyableReference?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .ToList();
    }
}
