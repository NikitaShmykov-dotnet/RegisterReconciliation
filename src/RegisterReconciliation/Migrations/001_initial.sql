create table import_batches (
    id bigint generated always as identity primary key,
    source text not null check (source in ('internal', 'external')),
    digest bytea not null,
    imported_at timestamptz not null default now(),
    row_count integer not null default 0,
    invalid_count integer not null default 0,
    unique (source, digest)
);

create table imported_rows (
    id bigint generated always as identity primary key,
    batch_id bigint not null references import_batches(id),
    line integer not null check (line >= 2),
    raw text not null,
    external_id text,
    amount numeric(18, 2),
    currency text,
    occurred_at timestamptz,
    order_reference text,
    error text,
    unique (batch_id, line),
    check (error is not null or
           (amount is not null and currency is not null and occurred_at is not null and order_reference is not null))
);

create table reconciliation_runs (
    id bigint generated always as identity primary key,
    internal_batch_id bigint not null references import_batches(id),
    external_batch_id bigint not null references import_batches(id),
    algorithm_version integer not null,
    started_at timestamptz not null default now()
);

create table outcomes (
    run_id bigint not null references reconciliation_runs(id),
    position integer not null,
    category text not null check (category in
        ('matched', 'missing-internal', 'missing-external', 'amount-mismatch', 'duplicate', 'ambiguous')),
    method text,
    internal_row_id bigint references imported_rows(id),
    external_row_id bigint references imported_rows(id),
    candidate_count integer not null,
    primary key (run_id, position),
    unique (run_id, internal_row_id),
    unique (run_id, external_row_id),
    check (internal_row_id is not null or external_row_id is not null),
    check ((category in ('matched', 'amount-mismatch')) =
           (internal_row_id is not null and external_row_id is not null))
);

create table resolutions (
    run_id bigint not null,
    position integer not null,
    author text not null,
    resolved_at timestamptz not null default clock_timestamp(),
    comment text not null check (length(trim(comment)) between 1 and 1000),
    primary key (run_id, position),
    foreign key (run_id, position) references outcomes(run_id, position)
);
