using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Clauses;

public sealed class ProcessingRunConfiguration : IEntityTypeConfiguration<ProcessingRun>
{
    public void Configure(EntityTypeBuilder<ProcessingRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("processing_run", "platform");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.Id).HasColumnName("id");
        builder.Property(run => run.DocumentId).HasColumnName("document_id");
        builder.Property(run => run.CompanyId).HasColumnName("company_id");
        builder.Property(run => run.RequestedBy).HasColumnName("requested_by");
        builder.Property(run => run.Status).HasColumnName("status");
        builder.Property(run => run.Stage).HasColumnName("stage").IsRequired();
        builder.Property(run => run.Provider).HasColumnName("provider");
        builder.Property(run => run.Model).HasColumnName("model");
        builder.Property(run => run.PromptVersion).HasColumnName("prompt_version");
        builder.Property(run => run.Attempts).HasColumnName("attempts");
        builder.Property(run => run.ClauseCount).HasColumnName("clause_count");
        builder.Property(run => run.ClassifiedCount).HasColumnName("classified_count");
        builder.Property(run => run.UnclassifiedCount).HasColumnName("unclassified_count");
        builder.Property(run => run.InputTokens).HasColumnName("input_tokens");
        builder.Property(run => run.OutputTokens).HasColumnName("output_tokens");
        builder.Property(run => run.ErrorCode).HasColumnName("error_code");
        builder.Property(run => run.ErrorMessage).HasColumnName("error_message");
        builder.Property(run => run.CreatedAt).HasColumnName("created_at");
        builder.Property(run => run.StartedAt).HasColumnName("started_at");
        builder.Property(run => run.FinishedAt).HasColumnName("finished_at");
        builder.Property(run => run.UpdatedAt).HasColumnName("updated_at");
        builder.Property(run => run.AvailableAt).HasColumnName("available_at");
        builder.Property(run => run.LeaseExpiresAt).HasColumnName("lease_expires_at");
    }
}

public sealed class ProcessingAttemptConfiguration : IEntityTypeConfiguration<ProcessingAttempt>
{
    public void Configure(EntityTypeBuilder<ProcessingAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("processing_attempt", "platform");
        builder.HasKey(attempt => attempt.Id);

        builder.Property(attempt => attempt.Id).HasColumnName("id");
        builder.Property(attempt => attempt.RunId).HasColumnName("run_id");
        builder.Property(attempt => attempt.CompanyId).HasColumnName("company_id");
        builder.Property(attempt => attempt.AttemptNo).HasColumnName("attempt_no");
        builder.Property(attempt => attempt.Status).HasColumnName("status");
        builder.Property(attempt => attempt.ErrorCode).HasColumnName("error_code");
        builder.Property(attempt => attempt.ErrorMessage).HasColumnName("error_message");
        builder.Property(attempt => attempt.InputTokens).HasColumnName("input_tokens");
        builder.Property(attempt => attempt.OutputTokens).HasColumnName("output_tokens");
        builder.Property(attempt => attempt.LatencyMs).HasColumnName("latency_ms");
        builder.Property(attempt => attempt.ProviderRequestId).HasColumnName("provider_request_id");
        builder.Property(attempt => attempt.StartedAt).HasColumnName("started_at");
        builder.Property(attempt => attempt.FinishedAt).HasColumnName("finished_at");
    }
}

public sealed class ExtractedClauseConfiguration : IEntityTypeConfiguration<ExtractedClause>
{
    public void Configure(EntityTypeBuilder<ExtractedClause> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("extracted_clause", "platform");
        builder.HasKey(clause => clause.Id);

        builder.Property(clause => clause.Id).HasColumnName("id");
        builder.Property(clause => clause.RunId).HasColumnName("run_id");
        builder.Property(clause => clause.DocumentId).HasColumnName("document_id");
        builder.Property(clause => clause.CompanyId).HasColumnName("company_id");
        builder.Property(clause => clause.Ordinal).HasColumnName("ordinal");
        builder.Property(clause => clause.ClauseNumber).HasColumnName("clause_number");
        builder.Property(clause => clause.Heading).HasColumnName("heading");
        builder.Property(clause => clause.Text).HasColumnName("text").IsRequired();
        builder.Property(clause => clause.PageFrom).HasColumnName("page_from");
        builder.Property(clause => clause.PageTo).HasColumnName("page_to");
        builder.Property(clause => clause.SourceCharStart).HasColumnName("source_char_start");
        builder.Property(clause => clause.SourceCharEnd).HasColumnName("source_char_end");
        builder.Property(clause => clause.SourceBoxes).HasColumnName("source_boxes").HasColumnType("jsonb");
        builder.Property(clause => clause.Confidence).HasColumnName("confidence").HasColumnType("numeric(4,3)");
        builder.Property(clause => clause.NeedsReview).HasColumnName("needs_review");
        builder.Property(clause => clause.ReviewStatus).HasColumnName("review_status");
        builder.Property(clause => clause.CreatedAt).HasColumnName("created_at");
    }
}

public sealed class ClauseCategoryConfiguration : IEntityTypeConfiguration<ClauseCategory>
{
    public void Configure(EntityTypeBuilder<ClauseCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("clause_category", "platform");
        builder.HasKey(category => category.Id);

        builder.Property(category => category.Id).HasColumnName("id");
        builder.Property(category => category.Key).HasColumnName("key").IsRequired();
        builder.Property(category => category.Name).HasColumnName("name").IsRequired();
        builder.Property(category => category.Description).HasColumnName("description");
        builder.Property(category => category.ParentId).HasColumnName("parent_id");
        builder.Property(category => category.SortOrder).HasColumnName("sort_order");
        builder.Property(category => category.IsActive).HasColumnName("is_active");
        builder.Property(category => category.CreatedAt).HasColumnName("created_at");
        builder.Property(category => category.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class ClauseClassificationConfiguration : IEntityTypeConfiguration<ClauseClassification>
{
    public void Configure(EntityTypeBuilder<ClauseClassification> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("clause_classification", "platform");
        builder.HasKey(classification => classification.Id);

        builder.Property(classification => classification.Id).HasColumnName("id");
        builder.Property(classification => classification.ClauseId).HasColumnName("clause_id");
        builder.Property(classification => classification.CategoryId).HasColumnName("category_id");
        builder.Property(classification => classification.CompanyId).HasColumnName("company_id");
        builder.Property(classification => classification.Confidence).HasColumnName("confidence").HasColumnType("numeric(4,3)");
        builder.Property(classification => classification.IsPrimary).HasColumnName("is_primary");
        builder.Property(classification => classification.Rationale).HasColumnName("rationale");
        builder.Property(classification => classification.Model).HasColumnName("model");
        builder.Property(classification => classification.PromptVersion).HasColumnName("prompt_version");
        builder.Property(classification => classification.CreatedAt).HasColumnName("created_at");
    }
}

public sealed class CompanyQuotaConfiguration : IEntityTypeConfiguration<CompanyQuota>
{
    public void Configure(EntityTypeBuilder<CompanyQuota> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("company_quota", "platform");
        builder.HasKey(quota => quota.CompanyId);
        builder.Property(quota => quota.CompanyId).HasColumnName("company_id");
        builder.Property(quota => quota.DocumentsLimit).HasColumnName("documents_limit");
        builder.Property(quota => quota.PagesLimit).HasColumnName("pages_limit");
        builder.Property(quota => quota.TokensLimit).HasColumnName("tokens_limit");
        builder.Property(quota => quota.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class ProcessingUsageConfiguration : IEntityTypeConfiguration<ProcessingUsage>
{
    public void Configure(EntityTypeBuilder<ProcessingUsage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("processing_usage", "platform");
        builder.HasKey(usage => new { usage.CompanyId, usage.Period });
        builder.Property(usage => usage.CompanyId).HasColumnName("company_id");
        builder.Property(usage => usage.Period).HasColumnName("period");
        builder.Property(usage => usage.Documents).HasColumnName("documents");
        builder.Property(usage => usage.Pages).HasColumnName("pages");
        builder.Property(usage => usage.InputTokens).HasColumnName("input_tokens");
        builder.Property(usage => usage.OutputTokens).HasColumnName("output_tokens");
        builder.Property(usage => usage.CostEstimateUsd).HasColumnName("cost_estimate_usd").HasColumnType("numeric(12,6)");
        builder.Property(usage => usage.UpdatedAt).HasColumnName("updated_at");
    }
}