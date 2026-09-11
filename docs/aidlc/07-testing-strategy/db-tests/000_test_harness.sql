-- Fantasy EPL League Manager — Database Invariant Test Harness
--
-- A minimal, dependency-free assertion framework (no pgTAP extension required, since the target
-- deployment image is unknown pre-hosting-decision, Architecture ADR-005) for the database-level
-- invariant tests in this folder. Run this file first, then each numbered test file in order,
-- then call test_summary() to get a pass/fail report and a nonzero exit via RAISE EXCEPTION if
-- anything failed (suitable for a CI gate once one exists).

CREATE TABLE IF NOT EXISTS test_results (
  test_id    serial PRIMARY KEY,
  test_name  text NOT NULL,
  passed     boolean NOT NULL,
  detail     text
);

CREATE OR REPLACE FUNCTION test_assert(p_name text, p_condition boolean, p_detail text DEFAULT NULL)
RETURNS void AS $$
BEGIN
  INSERT INTO test_results (test_name, passed, detail) VALUES (p_name, p_condition, p_detail);
  IF p_condition THEN
    RAISE NOTICE 'PASS: %', p_name;
  ELSE
    RAISE WARNING 'FAIL: % (%)', p_name, COALESCE(p_detail, 'no detail');
  END IF;
END;
$$ LANGUAGE plpgsql;

-- Convenience for the common "this statement must be rejected with SQLSTATE X" shape used
-- throughout these files: PERFORM test_assert(name, true) inside the matching EXCEPTION WHEN
-- branch, and PERFORM test_assert(name, false, 'statement unexpectedly succeeded') immediately
-- after the guarded statement if no exception was raised.

CREATE OR REPLACE FUNCTION test_summary() RETURNS void AS $$
DECLARE
  v_total  integer;
  v_passed integer;
  v_failed integer;
BEGIN
  SELECT count(*), count(*) FILTER (WHERE passed), count(*) FILTER (WHERE NOT passed)
    INTO v_total, v_passed, v_failed
  FROM test_results;

  RAISE NOTICE '=== db-tests summary: % passed, % failed, % total ===', v_passed, v_failed, v_total;

  IF v_failed > 0 THEN
    RAISE EXCEPTION '% database invariant test(s) failed — see NOTICE/WARNING log above for detail', v_failed;
  END IF;
END;
$$ LANGUAGE plpgsql;
