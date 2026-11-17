CREATE OR REPLACE FUNCTION business_days_between(p_from IN DATE, p_to IN DATE)
  RETURN PLS_INTEGER
  DETERMINISTIC
IS
  l_days PLS_INTEGER := 0;
  l_day  DATE := TRUNC(LEAST(p_from, p_to));
BEGIN
  WHILE l_day < TRUNC(GREATEST(p_from, p_to)) LOOP
    IF TO_CHAR(l_day, 'DY', 'NLS_DATE_LANGUAGE=ENGLISH') NOT IN ('SAT', 'SUN') THEN
      l_days := l_days + 1;
    END IF;
    l_day := l_day + 1;
  END LOOP;
  RETURN l_days;
END business_days_between;
/

CREATE OR REPLACE PROCEDURE purge_old_logs(
  p_keep_days IN PLS_INTEGER DEFAULT 90,
  p_dry_run   IN BOOLEAN DEFAULT FALSE
) AUTHID CURRENT_USER
AS
  l_cutoff CONSTANT DATE := TRUNC(SYSDATE) - p_keep_days;
  l_deleted PLS_INTEGER;
BEGIN
  LOOP
    DELETE FROM app_logs WHERE logged_at < l_cutoff AND ROWNUM <= 10000;
    l_deleted := SQL%ROWCOUNT;
    IF p_dry_run THEN
      ROLLBACK;
      EXIT;
    END IF;
    COMMIT;
    EXIT WHEN l_deleted = 0;
  END LOOP;
END;
/

CREATE OR REPLACE VIEW card_summary_v (card_id, card_no, holder_name, balance, status_name, last_txn_at) AS
SELECT c.card_id,
       c.card_no,
       h.full_name,
       c.balance,
       card_types.status_name(c.status),
       (SELECT MAX(t.created_at) FROM transactions t WHERE t.card_id = c.card_id)
  FROM cards c
  JOIN card_holders h ON h.holder_id = c.holder_id
 WHERE c.status <> 'C'
  WITH READ ONLY;

-- Anonymous block with nested blocks, labels and several loop forms.
<<outer_block>>
DECLARE
  l_counter PLS_INTEGER := 0;
  l_text    VARCHAR2(200) := q'{It's a "quoted" string with {braces}}';
  l_alt     VARCHAR2(200) := q'!another one: it's fine!';
  l_nchar   NVARCHAR2(50) := N'ünïcödé';
  l_number  NUMBER := 1.5e-3;
  l_hex     RAW(4) := HEXTORAW('DEADBEEF');
  l_ts      TIMESTAMP WITH LOCAL TIME ZONE := SYSTIMESTAMP;
  l_iv      INTERVAL YEAR TO MONTH := INTERVAL '1-6' YEAR TO MONTH;
  l_bool    BOOLEAN;
  l_clob    CLOB;
  SUBTYPE t_small IS PLS_INTEGER RANGE 1 .. 10;
  l_small   t_small := 3;
BEGIN
  <<inner_block>>
  DECLARE
    l_counter PLS_INTEGER := 100;
  BEGIN
    outer_block.l_counter := l_counter + inner_block.l_counter;
  END inner_block;

  FOR i IN 1 .. 10 LOOP
    CONTINUE WHEN MOD(i, 2) = 0;
    l_counter := l_counter + i ** 2;
  END LOOP;

  <<rows_loop>>
  FOR r IN (SELECT LEVEL AS n FROM dual CONNECT BY LEVEL <= 5) LOOP
    <<cols_loop>>
    FOR c IN REVERSE 1 .. 3 LOOP
      EXIT rows_loop WHEN r.n * c > 12;
      CONTINUE rows_loop WHEN c = r.n;
      l_counter := l_counter - 1;
    END LOOP cols_loop;
  END LOOP rows_loop;

  l_bool := l_counter BETWEEN 0 AND 1000
            AND l_text LIKE 'It%'
            AND l_alt IS NOT NULL
            AND NOT (l_small IN (1, 2) OR l_number != 0.0015);

  l_clob := TO_CLOB(l_text);
  DBMS_LOB.APPEND(l_clob, CHR(13) || CHR(10) || UTL_RAW.CAST_TO_VARCHAR2(l_hex));

  CASE
    WHEN l_bool THEN
      DBMS_OUTPUT.PUT_LINE('ok: ' || l_counter || ' ' || TO_CHAR(l_ts, 'TZR') || ' ' || l_iv);
    WHEN l_bool IS NULL THEN
      NULL;
    ELSE
      RAISE PROGRAM_ERROR;
  END CASE;

  $IF DBMS_DB_VERSION.VER_LE_11_2 $THEN
    DBMS_OUTPUT.PUT_LINE('legacy database');
  $ELSIF $$debug_mode $THEN
    DBMS_OUTPUT.PUT_LINE('debug at line ' || $$PLSQL_LINE || ' in ' || $$PLSQL_UNIT);
  $ELSE
    NULL;
  $END
EXCEPTION
  WHEN VALUE_ERROR OR INVALID_NUMBER THEN
    DBMS_OUTPUT.PUT_LINE(DBMS_UTILITY.FORMAT_ERROR_STACK);
  WHEN OTHERS THEN
    IF SQLCODE = -1 THEN
      NULL;
    ELSE
      RAISE;
    END IF;
END outer_block;
/

DECLARE
  TYPE t_matrix IS TABLE OF SYS.ODCINUMBERLIST INDEX BY PLS_INTEGER;
  l_matrix t_matrix;
  l_cursor SYS_REFCURSOR;
  l_id     NUMBER;
  l_name   VARCHAR2(100);

  FUNCTION fact(n IN PLS_INTEGER) RETURN NUMBER IS
  BEGIN
    RETURN CASE WHEN n <= 1 THEN 1 ELSE n * fact(n - 1) END;
  END fact;

  PROCEDURE show(p_text IN VARCHAR2 := 'empty') IS
  BEGIN
    DBMS_OUTPUT.PUT_LINE(p_text);
  END;
BEGIN
  l_matrix(1) := SYS.ODCINUMBERLIST(1, 2, 3);
  l_matrix(2) := SYS.ODCINUMBERLIST(fact(3), fact(4));
  show(l_matrix(2)(1) || '/' || l_matrix(1).COUNT);
  show;

  OPEN l_cursor FOR SELECT holder_id, full_name FROM card_holders WHERE ROWNUM <= 3;
  LOOP
    FETCH l_cursor INTO l_id, l_name;
    EXIT WHEN l_cursor%NOTFOUND;
    show(l_cursor%ROWCOUNT || ': ' || l_id || ' ' || INITCAP(l_name));
  END LOOP;
  CLOSE l_cursor;

  EXECUTE IMMEDIATE 'ALTER SESSION SET CURRENT_SCHEMA = APP';
  SET TRANSACTION READ ONLY NAME 'report';
  COMMIT;
END;
/
