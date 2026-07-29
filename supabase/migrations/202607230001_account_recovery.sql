-- Account aliases and recovery metadata for Comote web authentication.
-- Authentication emails remain private; clients can only read their own row.

begin;

create table if not exists public.account_profiles (
    user_id uuid primary key references auth.users(id) on delete cascade,
    account_id text not null,
    recovery_email text not null,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    constraint account_profiles_account_id_format
        check (account_id ~ '^[a-z0-9][a-z0-9._-]{3,31}$'),
    constraint account_profiles_account_id_unique unique (account_id),
    constraint account_profiles_recovery_email_unique unique (recovery_email)
);

create index if not exists account_profiles_recovery_email_idx
    on public.account_profiles (recovery_email);

alter table public.account_profiles enable row level security;

drop policy if exists "users read own account profile" on public.account_profiles;
create policy "users read own account profile"
on public.account_profiles
for select
to authenticated
using (user_id = auth.uid());

create or replace function public.handle_comote_auth_user()
returns trigger
language plpgsql
security definer
set search_path = public, auth
as $$
declare
    requested_account_id text;
begin
    requested_account_id := lower(trim(coalesce(new.raw_user_meta_data ->> 'account_id', '')));

    if requested_account_id = '' and new.email like '%@accounts.kymote.app' then
        requested_account_id := lower(split_part(new.email, '@', 1));
    end if;

    -- Existing email-only users remain valid. New web signups always provide an ID.
    if requested_account_id = '' then
        return new;
    end if;

    if requested_account_id !~ '^[a-z0-9][a-z0-9._-]{3,31}$' then
        raise exception 'invalid_account_id';
    end if;

    insert into public.account_profiles (user_id, account_id, recovery_email)
    values (new.id, requested_account_id, lower(new.email))
    on conflict (user_id) do update
       set recovery_email = excluded.recovery_email,
           updated_at = now();

    return new;
end;
$$;

drop trigger if exists comote_auth_user_created on auth.users;
create trigger comote_auth_user_created
after insert on auth.users
for each row execute function public.handle_comote_auth_user();

create or replace function public.sync_comote_auth_email()
returns trigger
language plpgsql
security definer
set search_path = public, auth
as $$
begin
    if new.email is distinct from old.email then
        update public.account_profiles
           set recovery_email = lower(new.email),
               updated_at = now()
         where user_id = new.id;
    end if;
    return new;
end;
$$;

drop trigger if exists comote_auth_email_updated on auth.users;
create trigger comote_auth_email_updated
after update of email on auth.users
for each row execute function public.sync_comote_auth_email();

-- Preserve ID-only accounts created by earlier Comote previews.
insert into public.account_profiles (user_id, account_id, recovery_email)
select id, lower(split_part(email, '@', 1)), lower(email)
from auth.users
where email like '%@accounts.kymote.app'
on conflict do nothing;

commit;
