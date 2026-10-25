-- =====================================================================================
-- 20260106 — T12: news publication is a privileged transition, recorded by the database
--
-- The API already gates publish/unpublish behind news.publish, but RLS cannot see a column change, so
-- the API role could set state = 'Published' with a direct UPDATE. The agreement and negotiation state
-- changes already have this guard; news did not, and news is the content the anonymous public surface
-- serves.
--
-- The trigger also *assigns* published_by and published_at: T12 AC 3 says the actor and the time are
-- recorded by the database, not the client. On unpublish it clears them, because the table's
-- consistency CHECK requires exactly that.
-- =====================================================================================

create or replace function app.guard_news_publication()
returns trigger
language plpgsql
as $$
begin
    if tg_op = 'INSERT' then
        -- The legacy create hardcoded 'Pendiente' and publishing required a second edit (BR-25). The
        -- inverse is just as bad: an article inserted as Published would never pass through the
        -- capability check.
        if new.state <> 'Draft' then
            raise exception
                'a news article is created as a draft and published through the publish operation'
                using errcode = 'invalid_parameter_value';
        end if;
        return new;
    end if;

    if new.state is distinct from old.state then
        if not platform.has_capability('news.publish') then
            raise exception
                'capability news.publish is required to change a news publication state'
                using errcode = 'insufficient_privilege';
        end if;

        if new.state = 'Published' then
            new.published_by := platform.current_user_id();
            new.published_at := now();
        else
            new.published_by := null;
            new.published_at := null;
        end if;
    end if;

    return new;
end;
$$;

comment on function app.guard_news_publication() is
  'Publish/unpublish requires news.publish at the database layer, and the database assigns the '
  'publication actor and timestamp (T12 AC 3). A news article is inserted as a draft and published '
  'through the publish operation.';

drop trigger if exists news_publication_guard on app.news_article;

create trigger news_publication_guard
    before insert or update on app.news_article
    for each row execute function app.guard_news_publication();
