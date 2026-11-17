CREATE OR REPLACE PACKAGE BODY settlement_etl AS

  c_limit CONSTANT PLS_INTEGER := 1000;

  e_bulk_errors EXCEPTION;
  PRAGMA EXCEPTION_INIT(e_bulk_errors, -24381);

  TYPE t_stage_tab IS TABLE OF settlement_stage%ROWTYPE;
  TYPE t_num_tab IS TABLE OF NUMBER INDEX BY PLS_INTEGER;
  TYPE t_rowid_tab IS TABLE OF ROWID INDEX BY PLS_INTEGER;
  TYPE t_totals IS TABLE OF NUMBER INDEX BY VARCHAR2(30);

  PROCEDURE log_error(p_step IN VARCHAR2, p_key IN VARCHAR2, p_code IN PLS_INTEGER, p_message IN VARCHAR2) IS
    PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    INSERT INTO etl_errors (error_id, step, key_value, error_code, error_message, logged_at)
    VALUES (etl_error_seq.NEXTVAL, p_step, p_key, p_code, SUBSTR(p_message, 1, 2000), SYSTIMESTAMP);
    COMMIT;
  END log_error;

  PROCEDURE load_stage(p_batch_id IN NUMBER, p_loaded OUT PLS_INTEGER, p_rejected OUT PLS_INTEGER) IS
    CURSOR c_src IS
      SELECT /*+ FULL(s) */ s.*
        FROM settlement_stage s
       WHERE s.batch_id = p_batch_id
         AND s.processed = 'N';
    l_rows t_stage_tab;
  BEGIN
    p_loaded := 0;
    p_rejected := 0;

    OPEN c_src;
    LOOP
      FETCH c_src BULK COLLECT INTO l_rows LIMIT c_limit;
      EXIT WHEN l_rows.COUNT = 0;

      BEGIN
        FORALL i IN 1 .. l_rows.COUNT SAVE EXCEPTIONS
          INSERT INTO settlements (settlement_id, card_id, amount, currency, settled_on, batch_id)
          VALUES (settlement_seq.NEXTVAL, l_rows(i).card_id, l_rows(i).amount, l_rows(i).currency, l_rows(i).txn_date, p_batch_id);
        p_loaded := p_loaded + SQL%ROWCOUNT;
      EXCEPTION
        WHEN e_bulk_errors THEN
          p_loaded := p_loaded + SQL%ROWCOUNT;
          p_rejected := p_rejected + SQL%BULK_EXCEPTIONS.COUNT;
          FOR j IN 1 .. SQL%BULK_EXCEPTIONS.COUNT LOOP
            log_error(
              'LOAD',
              TO_CHAR(l_rows(SQL%BULK_EXCEPTIONS(j).ERROR_INDEX).stage_id),
              SQL%BULK_EXCEPTIONS(j).ERROR_CODE,
              SQLERRM(-SQL%BULK_EXCEPTIONS(j).ERROR_CODE));
          END LOOP;
      END;

      COMMIT;
    END LOOP;
    CLOSE c_src;

    UPDATE settlement_stage SET processed = 'Y' WHERE batch_id = p_batch_id;
    COMMIT;
  END load_stage;

  PROCEDURE apply_adjustments(p_batch_id IN NUMBER) IS
    l_ids     t_num_tab;
    l_amounts t_num_tab;
    l_keep    t_num_tab;
    l_rowids  t_rowid_tab;
  BEGIN
    SELECT a.settlement_id, a.delta, a.ROWID
      BULK COLLECT INTO l_ids, l_amounts, l_rowids
      FROM adjustments a
     WHERE a.batch_id = p_batch_id
       AND a.applied_at IS NULL;

    -- Only non-zero adjustments are applied; the index collection is sparse.
    FOR i IN 1 .. l_ids.COUNT LOOP
      IF l_amounts(i) <> 0 THEN
        l_keep(i) := i;
      END IF;
    END LOOP;

    FORALL i IN INDICES OF l_keep
      UPDATE settlements s
         SET s.amount = s.amount + l_amounts(i),
             s.adjusted = 'Y'
       WHERE s.settlement_id = l_ids(i);

    FORALL i IN VALUES OF l_keep
      UPDATE adjustments
         SET applied_at = SYSDATE
       WHERE ROWID = l_rowids(i);

    FOR i IN 1 .. l_ids.COUNT LOOP
      IF SQL%BULK_ROWCOUNT(i) = 0 THEN
        log_error('ADJUST', TO_CHAR(l_ids(i)), 0, 'No row updated');
      END IF;
    END LOOP;
  END apply_adjustments;

  FUNCTION totals_by_currency(p_batch_id IN NUMBER) RETURN VARCHAR2 IS
    l_totals   t_totals;
    l_currency VARCHAR2(30);
    l_text     VARCHAR2(4000);
  BEGIN
    FOR r IN (SELECT currency, amount FROM settlements WHERE batch_id = p_batch_id) LOOP
      IF l_totals.EXISTS(r.currency) THEN
        l_totals(r.currency) := l_totals(r.currency) + r.amount;
      ELSE
        l_totals(r.currency) := r.amount;
      END IF;
    END LOOP;

    l_currency := l_totals.FIRST;
    WHILE l_currency IS NOT NULL LOOP
      l_text := l_text || l_currency || '=' || TO_CHAR(l_totals(l_currency), 'FM999999990.00') || ';';
      l_currency := l_totals.NEXT(l_currency);
    END LOOP;

    RETURN RTRIM(l_text, ';');
  END totals_by_currency;

  PROCEDURE copy_with_error_log(p_batch_id IN NUMBER) IS
  BEGIN
    INSERT /*+ APPEND */ INTO settlement_archive (settlement_id, card_id, amount, currency, settled_on)
    SELECT s.settlement_id, s.card_id, s.amount, s.currency, s.settled_on
      FROM settlements s
     WHERE s.batch_id = p_batch_id
       LOG ERRORS INTO err$_settlement_archive ('batch ' || p_batch_id) REJECT LIMIT UNLIMITED;
    COMMIT;

    EXECUTE IMMEDIATE 'TRUNCATE TABLE settlement_stage_tmp DROP STORAGE';

    LOCK TABLE settlement_control IN EXCLUSIVE MODE NOWAIT;
    UPDATE settlement_control
       SET last_batch_id = p_batch_id,
           last_run_at = SYSTIMESTAMP AT TIME ZONE 'UTC'
     WHERE control_id = 1;
    COMMIT WORK;
  END copy_with_error_log;

  PROCEDURE reconcile(p_batch_id IN NUMBER, p_tolerance IN NUMBER DEFAULT 0.01) IS
    l_expected NUMBER;
    l_actual   NUMBER;
    l_attempt  PLS_INTEGER := 0;
  BEGIN
    <<retry>>
    LOOP
      l_attempt := l_attempt + 1;

      SELECT SUM(amount) INTO l_expected FROM settlement_stage WHERE batch_id = p_batch_id;
      SELECT SUM(amount) INTO l_actual FROM settlements WHERE batch_id = p_batch_id;

      EXIT retry WHEN ABS(NVL(l_expected, 0) - NVL(l_actual, 0)) <= p_tolerance;

      IF l_attempt >= 3 THEN
        GOTO failed;
      END IF;

      apply_adjustments(p_batch_id);
    END LOOP retry;

    RETURN;

    <<failed>>
    log_error('RECONCILE', TO_CHAR(p_batch_id), -1, 'expected ' || l_expected || ' actual ' || l_actual);
    RAISE_APPLICATION_ERROR(-20500, 'Batch ' || p_batch_id || ' does not reconcile');
  END reconcile;

END settlement_etl;
/
