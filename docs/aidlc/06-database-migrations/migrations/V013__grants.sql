-- Fantasy EPL League Manager — Database Migrations
-- V013: Application role grants (BR-172, ADR-010). Runs last, after every table exists.

GRANT USAGE ON SCHEMA public TO eplfantasy_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO eplfantasy_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO eplfantasy_app;

-- BR-149/ADR-010: administrative_actions is append-only at the database role level, not just by
-- application convention — the runtime role can INSERT/SELECT but is explicitly denied UPDATE/DELETE.
REVOKE UPDATE, DELETE ON administrative_actions FROM eplfantasy_app;

-- Applies the same default grant set to any table a later migration adds, so V014+ doesn't need to
-- repeat this file.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO eplfantasy_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO eplfantasy_app;
