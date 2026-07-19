-- Comote backend bootstrap for a new Supabase project.
-- Run with the Supabase CLI or paste into the SQL editor once.

begin;

create extension if not exists pgcrypto;

create table if not exists public.hosts (
    user_id uuid not null references auth.users(id) on delete cascade,
    host_id text not null,
    host_name text not null,
    ip text,
    internal_ip text,
    resolution text,
    cpu integer not null default 0 check (cpu between 0 and 100),
    ram text,
    hdd text,
    uptime text,
    mac_address text,
    agent_version text,
    monitor_count integer not null default 1 check (monitor_count > 0),
    thumbnail_path text,
    thumbnail_url text,
    last_seen timestamptz not null default now(),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    primary key (user_id, host_id),
    constraint hosts_host_id_format check (host_id ~ '^[A-Za-z0-9_-]{3,128}$')
);

create index if not exists hosts_user_last_seen_idx
    on public.hosts (user_id, last_seen desc);

create table if not exists public.device_groups (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references auth.users(id) on delete cascade,
    name text not null,
    description text,
    sort_order integer not null default 0,
    layout_columns integer not null default 5 check (layout_columns between 1 and 20),
    tile_aspect_ratio numeric(6, 3) not null default 1.778,
    monitoring_fps integer not null default 2 check (monitoring_fps between 1 and 10),
    monitoring_quality integer not null default 50 check (monitoring_quality between 10 and 100),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    unique (user_id, name)
);

create table if not exists public.device_group_members (
    user_id uuid not null references auth.users(id) on delete cascade,
    group_id uuid not null references public.device_groups(id) on delete cascade,
    host_id text not null,
    sort_order integer not null default 0,
    created_at timestamptz not null default now(),
    primary key (group_id, host_id),
    foreign key (user_id, host_id)
        references public.hosts(user_id, host_id)
        on delete cascade
);

create index if not exists device_group_members_user_host_idx
    on public.device_group_members (user_id, host_id);

create type public.remote_job_type as enum (
    'file_upload',
    'process_start',
    'process_stop',
    'restart',
    'shutdown',
    'logoff',
    'lock'
);

create type public.remote_job_status as enum (
    'queued',
    'running',
    'succeeded',
    'failed',
    'cancelled',
    'expired'
);

create table if not exists public.remote_jobs (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references auth.users(id) on delete cascade,
    created_by uuid not null references auth.users(id) on delete cascade,
    job_type public.remote_job_type not null,
    payload jsonb not null default '{}'::jsonb,
    status public.remote_job_status not null default 'queued',
    expires_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists public.remote_job_targets (
    job_id uuid not null references public.remote_jobs(id) on delete cascade,
    user_id uuid not null references auth.users(id) on delete cascade,
    host_id text not null,
    status public.remote_job_status not null default 'queued',
    progress integer not null default 0 check (progress between 0 and 100),
    result_code text,
    result_message text,
    retry_count integer not null default 0,
    started_at timestamptz,
    completed_at timestamptz,
    updated_at timestamptz not null default now(),
    primary key (job_id, host_id),
    foreign key (user_id, host_id)
        references public.hosts(user_id, host_id)
        on delete cascade
);

create table if not exists public.audit_events (
    id bigint generated always as identity primary key,
    user_id uuid not null references auth.users(id) on delete cascade,
    host_id text,
    action text not null,
    summary text,
    metadata jsonb not null default '{}'::jsonb,
    created_at timestamptz not null default now()
);

create or replace function public.set_updated_at()
returns trigger
language plpgsql
as $$
begin
    new.updated_at = now();
    return new;
end;
$$;

drop trigger if exists hosts_set_updated_at on public.hosts;
create trigger hosts_set_updated_at
before update on public.hosts
for each row execute function public.set_updated_at();

drop trigger if exists device_groups_set_updated_at on public.device_groups;
create trigger device_groups_set_updated_at
before update on public.device_groups
for each row execute function public.set_updated_at();

drop trigger if exists remote_jobs_set_updated_at on public.remote_jobs;
create trigger remote_jobs_set_updated_at
before update on public.remote_jobs
for each row execute function public.set_updated_at();

drop trigger if exists remote_job_targets_set_updated_at on public.remote_job_targets;
create trigger remote_job_targets_set_updated_at
before update on public.remote_job_targets
for each row execute function public.set_updated_at();

alter table public.hosts enable row level security;
alter table public.device_groups enable row level security;
alter table public.device_group_members enable row level security;
alter table public.remote_jobs enable row level security;
alter table public.remote_job_targets enable row level security;
alter table public.audit_events enable row level security;

create policy "users manage own hosts"
on public.hosts
for all
to authenticated
using (user_id = auth.uid())
with check (user_id = auth.uid());

create policy "users manage own groups"
on public.device_groups
for all
to authenticated
using (user_id = auth.uid())
with check (user_id = auth.uid());

create policy "users manage own group members"
on public.device_group_members
for all
to authenticated
using (user_id = auth.uid())
with check (
    user_id = auth.uid()
    and exists (
        select 1
        from public.device_groups groups
        where groups.id = group_id
          and groups.user_id = auth.uid()
    )
);

create policy "users manage own jobs"
on public.remote_jobs
for all
to authenticated
using (user_id = auth.uid())
with check (user_id = auth.uid() and created_by = auth.uid());

create policy "users manage own job targets"
on public.remote_job_targets
for all
to authenticated
using (user_id = auth.uid())
with check (user_id = auth.uid());

create policy "users read own audit events"
on public.audit_events
for select
to authenticated
using (user_id = auth.uid());

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values (
    'thumbnails',
    'thumbnails',
    false,
    1048576,
    array['image/jpeg', 'image/webp']
)
on conflict (id) do update
set public = false,
    file_size_limit = excluded.file_size_limit,
    allowed_mime_types = excluded.allowed_mime_types;

create policy "users read own thumbnails"
on storage.objects
for select
to authenticated
using (
    bucket_id = 'thumbnails'
    and (storage.foldername(name))[1] = auth.uid()::text
);

create policy "users upload own thumbnails"
on storage.objects
for insert
to authenticated
with check (
    bucket_id = 'thumbnails'
    and (storage.foldername(name))[1] = auth.uid()::text
);

create policy "users update own thumbnails"
on storage.objects
for update
to authenticated
using (
    bucket_id = 'thumbnails'
    and (storage.foldername(name))[1] = auth.uid()::text
)
with check (
    bucket_id = 'thumbnails'
    and (storage.foldername(name))[1] = auth.uid()::text
);

create policy "users delete own thumbnails"
on storage.objects
for delete
to authenticated
using (
    bucket_id = 'thumbnails'
    and (storage.foldername(name))[1] = auth.uid()::text
);

commit;
