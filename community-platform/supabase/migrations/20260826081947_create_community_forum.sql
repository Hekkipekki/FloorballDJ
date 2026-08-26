begin;

create schema if not exists community_private;

create table community_private.threads (
  id uuid primary key default gen_random_uuid(),
  nickname text not null check (char_length(nickname) between 2 and 40),
  title text not null check (char_length(title) between 4 and 120),
  body text not null check (char_length(body) between 10 and 5000),
  edit_token_hash text not null,
  author_fingerprint text not null,
  status text not null default 'visible' check (status in ('visible', 'hidden', 'deleted')),
  locked boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table community_private.replies (
  id uuid primary key default gen_random_uuid(),
  thread_id uuid not null references community_private.threads(id) on delete cascade,
  nickname text not null check (char_length(nickname) between 2 and 40),
  body text not null check (char_length(body) between 2 and 3000),
  edit_token_hash text not null,
  author_fingerprint text not null,
  status text not null default 'visible' check (status in ('visible', 'hidden', 'deleted')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table community_private.reports (
  id bigint generated always as identity primary key,
  thread_id uuid references community_private.threads(id) on delete cascade,
  reply_id uuid references community_private.replies(id) on delete cascade,
  reason text not null check (char_length(reason) between 3 and 500),
  reporter_fingerprint text not null,
  created_at timestamptz not null default now(),
  resolved_at timestamptz,
  check ((thread_id is not null) <> (reply_id is not null))
);

create table community_private.rate_events (
  id bigint generated always as identity primary key,
  fingerprint text not null,
  action text not null,
  created_at timestamptz not null default now()
);

create index community_threads_visible_created_idx
  on community_private.threads (created_at desc) where status = 'visible';
create index community_replies_thread_created_idx
  on community_private.replies (thread_id, created_at) where status = 'visible';
create index community_rate_events_lookup_idx
  on community_private.rate_events (fingerprint, action, created_at desc);

alter table community_private.threads enable row level security;
alter table community_private.replies enable row level security;
alter table community_private.reports enable row level security;
alter table community_private.rate_events enable row level security;

revoke all on schema community_private from public, anon, authenticated;
revoke all on all tables in schema community_private from public, anon, authenticated;
revoke all on all sequences in schema community_private from public, anon, authenticated;

create or replace function public.community_enforce_rate_limit(
  p_fingerprint text,
  p_action text,
  p_limit integer,
  p_window_seconds integer
) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
  recent_count integer;
begin
  delete from community_private.rate_events
   where created_at < now() - interval '24 hours';

  select count(*) into recent_count
    from community_private.rate_events
   where fingerprint = p_fingerprint
     and action = p_action
     and created_at >= now() - make_interval(secs => greatest(1, p_window_seconds));

  if recent_count >= greatest(1, p_limit) then
    return false;
  end if;

  insert into community_private.rate_events(fingerprint, action)
  values (p_fingerprint, p_action);
  return true;
end;
$$;

create or replace function public.community_list_threads(p_limit integer default 25, p_offset integer default 0)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(jsonb_agg(row_data order by created_at desc), '[]'::jsonb)
  from (
    select jsonb_build_object(
      'id', t.id,
      'nickname', t.nickname,
      'title', t.title,
      'bodyPreview', left(t.body, 260),
      'locked', t.locked,
      'createdAt', t.created_at,
      'updatedAt', t.updated_at,
      'replyCount', (select count(*) from community_private.replies r where r.thread_id = t.id and r.status = 'visible')
    ) as row_data, t.created_at
    from community_private.threads t
    where t.status = 'visible'
    order by t.created_at desc
    limit least(greatest(p_limit, 1), 50)
    offset greatest(p_offset, 0)
  ) listed;
$$;

create or replace function public.community_get_thread(p_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select jsonb_build_object(
    'id', t.id,
    'nickname', t.nickname,
    'title', t.title,
    'body', t.body,
    'locked', t.locked,
    'createdAt', t.created_at,
    'updatedAt', t.updated_at,
    'replies', coalesce((
      select jsonb_agg(jsonb_build_object(
        'id', r.id,
        'nickname', r.nickname,
        'body', r.body,
        'createdAt', r.created_at,
        'updatedAt', r.updated_at
      ) order by r.created_at)
      from community_private.replies r
      where r.thread_id = t.id and r.status = 'visible'
    ), '[]'::jsonb)
  )
  from community_private.threads t
  where t.id = p_id and t.status = 'visible';
$$;

create or replace function public.community_create_thread(
  p_nickname text,
  p_title text,
  p_body text,
  p_edit_token_hash text,
  p_fingerprint text
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  created community_private.threads;
begin
  if not public.community_enforce_rate_limit(p_fingerprint, 'thread', 4, 3600) then
    raise exception 'RATE_LIMIT';
  end if;
  insert into community_private.threads(nickname, title, body, edit_token_hash, author_fingerprint)
  values (trim(p_nickname), trim(p_title), trim(p_body), p_edit_token_hash, p_fingerprint)
  returning * into created;
  return jsonb_build_object('id', created.id, 'createdAt', created.created_at);
end;
$$;

create or replace function public.community_create_reply(
  p_thread_id uuid,
  p_nickname text,
  p_body text,
  p_edit_token_hash text,
  p_fingerprint text
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  thread_row community_private.threads;
  created community_private.replies;
begin
  select * into thread_row from community_private.threads where id = p_thread_id and status = 'visible';
  if not found then raise exception 'THREAD_NOT_FOUND'; end if;
  if thread_row.locked then raise exception 'THREAD_LOCKED'; end if;
  if not public.community_enforce_rate_limit(p_fingerprint, 'reply', 12, 3600) then
    raise exception 'RATE_LIMIT';
  end if;
  insert into community_private.replies(thread_id, nickname, body, edit_token_hash, author_fingerprint)
  values (p_thread_id, trim(p_nickname), trim(p_body), p_edit_token_hash, p_fingerprint)
  returning * into created;
  return jsonb_build_object('id', created.id, 'createdAt', created.created_at);
end;
$$;

create or replace function public.community_delete_own_content(
  p_kind text,
  p_id uuid,
  p_edit_token_hash text
) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
begin
  if p_kind = 'thread' then
    update community_private.threads set status = 'deleted', body = '[Borttaget av författaren]', updated_at = now()
     where id = p_id and edit_token_hash = p_edit_token_hash and status = 'visible';
  elsif p_kind = 'reply' then
    update community_private.replies set status = 'deleted', body = '[Borttaget av författaren]', updated_at = now()
     where id = p_id and edit_token_hash = p_edit_token_hash and status = 'visible';
  else
    return false;
  end if;
  return found;
end;
$$;

create or replace function public.community_report_content(
  p_kind text,
  p_id uuid,
  p_reason text,
  p_fingerprint text
) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
begin
  if not public.community_enforce_rate_limit(p_fingerprint, 'report', 6, 3600) then
    raise exception 'RATE_LIMIT';
  end if;
  if p_kind = 'thread' then
    insert into community_private.reports(thread_id, reason, reporter_fingerprint)
    select id, trim(p_reason), p_fingerprint from community_private.threads where id = p_id;
  elsif p_kind = 'reply' then
    insert into community_private.reports(reply_id, reason, reporter_fingerprint)
    select id, trim(p_reason), p_fingerprint from community_private.replies where id = p_id;
  else
    return false;
  end if;
  return found;
end;
$$;

create or replace function public.community_admin_overview()
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select jsonb_build_object(
    'threads', coalesce((select jsonb_agg(jsonb_build_object(
      'id', t.id, 'nickname', t.nickname, 'title', t.title, 'body', t.body,
      'status', t.status, 'locked', t.locked, 'createdAt', t.created_at,
      'reportCount', (select count(*) from community_private.reports rp where rp.thread_id = t.id and rp.resolved_at is null)
    ) order by t.created_at desc) from community_private.threads t), '[]'::jsonb),
    'openReports', (select count(*) from community_private.reports where resolved_at is null)
  );
$$;

create or replace function public.community_admin_update_thread(
  p_id uuid,
  p_action text
) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
begin
  if p_action = 'hide' then update community_private.threads set status = 'hidden', updated_at = now() where id = p_id;
  elsif p_action = 'show' then update community_private.threads set status = 'visible', updated_at = now() where id = p_id;
  elsif p_action = 'lock' then update community_private.threads set locked = true, updated_at = now() where id = p_id;
  elsif p_action = 'unlock' then update community_private.threads set locked = false, updated_at = now() where id = p_id;
  elsif p_action = 'delete' then update community_private.threads set status = 'deleted', body = '[Raderat av moderator]', updated_at = now() where id = p_id;
  else return false;
  end if;
  return found;
end;
$$;

revoke all on function public.community_enforce_rate_limit(text, text, integer, integer) from public, anon, authenticated;
revoke all on function public.community_list_threads(integer, integer) from public, anon, authenticated;
revoke all on function public.community_get_thread(uuid) from public, anon, authenticated;
revoke all on function public.community_create_thread(text, text, text, text, text) from public, anon, authenticated;
revoke all on function public.community_create_reply(uuid, text, text, text, text) from public, anon, authenticated;
revoke all on function public.community_delete_own_content(text, uuid, text) from public, anon, authenticated;
revoke all on function public.community_report_content(text, uuid, text, text) from public, anon, authenticated;
revoke all on function public.community_admin_overview() from public, anon, authenticated;
revoke all on function public.community_admin_update_thread(uuid, text) from public, anon, authenticated;

grant usage on schema public to service_role;
grant execute on function public.community_list_threads(integer, integer) to service_role;
grant execute on function public.community_get_thread(uuid) to service_role;
grant execute on function public.community_create_thread(text, text, text, text, text) to service_role;
grant execute on function public.community_create_reply(uuid, text, text, text, text) to service_role;
grant execute on function public.community_delete_own_content(text, uuid, text) to service_role;
grant execute on function public.community_report_content(text, uuid, text, text) to service_role;
grant execute on function public.community_admin_overview() to service_role;
grant execute on function public.community_admin_update_thread(uuid, text) to service_role;

commit;
