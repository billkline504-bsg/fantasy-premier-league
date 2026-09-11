-- Fantasy EPL League Manager — Database Migrations
-- V001: Extensions and application role.
--
-- Inputs: Architecture and Domain Model v1.15, ADR-002 (EF Core/Npgsql), ADR-003 (PostgreSQL),
-- BR-172 (least privilege), BR-173 (secrets never in source control).
--
-- No login/password is created here. `eplfantasy_app` is a NOLOGIN group role that only carries
-- table/schema grants (see V013__grants.sql); an ops-provisioned login role is GRANTed membership
-- in it at deploy time, out of band from version control, satisfying BR-173. A separate, more
-- privileged role owns/runs these migrations (BR-172) — also provisioned out of band, not created
-- by application code.

CREATE EXTENSION IF NOT EXISTS pgcrypto; -- gen_random_uuid()

DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'eplfantasy_app') THEN
    CREATE ROLE eplfantasy_app NOLOGIN;
  END IF;
END
$$;
