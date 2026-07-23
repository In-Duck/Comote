begin;

create table if not exists public.connection_events (
    id bigint generated always as identity primary key,
    user_id uuid not null references auth.users(id) on delete cascade,
    host_id text,
    source text not null check (source in ('client', 'manager', 'web', 'turn', 'updater')),
    event_type text not null check (
        event_type in (
            'connected',
            'disconnected',
            'reconnecting',
            'recovered',
            'direct',
            'relayed',
            'degraded',
            'turn_unavailable',
            'update_started',
            'update_succeeded',
            'update_failed',
            'update_rolled_back'
        )
    ),
    severity text not null default 'info'
        check (severity in ('info', 'warning', 'critical')),
    details jsonb not null default '{}'::jsonb,
    created_at timestamptz not null default now()
);

create index if not exists connection_events_user_created_idx
    on public.connection_events (user_id, created_at desc);

create index if not exists connection_events_user_host_created_idx
    on public.connection_events (user_id, host_id, created_at desc);

alter table public.connection_events enable row level security;

create policy "users read own connection events"
on public.connection_events
for select
to authenticated
using (user_id = auth.uid());

create policy "users create own connection events"
on public.connection_events
for insert
to authenticated
with check (user_id = auth.uid());

create or replace function public.prune_connection_events()
returns integer
language plpgsql
security definer
set search_path = public
as $$
declare
    removed integer;
begin
    delete from public.connection_events
    where created_at < now() - interval '30 days';
    get diagnostics removed = row_count;
    return removed;
end;
$$;

revoke all on function public.prune_connection_events() from public;
grant execute on function public.prune_connection_events() to service_role;

commit;
