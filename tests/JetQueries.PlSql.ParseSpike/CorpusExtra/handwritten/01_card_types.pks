CREATE OR REPLACE PACKAGE card_types AUTHID DEFINER AS
  /**
   * Shared types, constants and exceptions for the card subsystem.
   * Kart alt sistemi için ortak tipler (ı, ğ, ş, ç, ö, ü, İ).
   */

  SUBTYPE t_card_no IS VARCHAR2(19);
  SUBTYPE t_amount IS NUMBER(12, 2);
  SUBTYPE t_flag IS CHAR(1) NOT NULL;

  c_status_active   CONSTANT VARCHAR2(1) := 'A';
  c_status_blocked  CONSTANT VARCHAR2(1) := 'B';
  c_status_closed   CONSTANT VARCHAR2(1) := 'C';
  c_max_balance     CONSTANT t_amount := 99999.99;
  c_default_ccy     CONSTANT VARCHAR2(3) := 'TRY';
  c_epoch           CONSTANT DATE := DATE '1970-01-01';
  c_timeout         CONSTANT INTERVAL DAY TO SECOND := INTERVAL '0 00:00:30' DAY TO SECOND;

  TYPE t_card_rec IS RECORD (
    card_id    cards.card_id%TYPE,
    card_no    t_card_no,
    balance    t_amount := 0,
    status     VARCHAR2(1) DEFAULT c_status_active,
    issued_at  DATE,
    holder     VARCHAR2(200 CHAR)
  );

  TYPE t_card_tab IS TABLE OF t_card_rec;
  TYPE t_card_map IS TABLE OF t_card_rec INDEX BY VARCHAR2(19);
  TYPE t_id_list IS VARRAY(1000) OF NUMBER(18);
  TYPE t_name_list IS TABLE OF VARCHAR2(100) INDEX BY PLS_INTEGER;
  TYPE t_card_cur IS REF CURSOR RETURN cards%ROWTYPE;
  TYPE t_weak_cur IS REF CURSOR;

  CURSOR c_active_cards(p_issuer_id IN NUMBER, p_since IN DATE DEFAULT SYSDATE - 30) RETURN cards%ROWTYPE;

  e_card_not_found        EXCEPTION;
  e_insufficient_balance  EXCEPTION;
  e_card_blocked          EXCEPTION;
  PRAGMA EXCEPTION_INIT(e_card_not_found, -20001);
  PRAGMA EXCEPTION_INIT(e_insufficient_balance, -20010);
  PRAGMA EXCEPTION_INIT(e_card_blocked, -20011);

  g_trace_enabled BOOLEAN := FALSE;

  FUNCTION mask_card_no(p_card_no IN t_card_no) RETURN VARCHAR2 DETERMINISTIC;

  FUNCTION is_valid_luhn(p_card_no IN t_card_no) RETURN BOOLEAN;

  FUNCTION to_minor_units(p_amount IN t_amount, p_ccy IN VARCHAR2 DEFAULT c_default_ccy)
    RETURN PLS_INTEGER
    DETERMINISTIC
    PARALLEL_ENABLE;

  FUNCTION status_name(p_status IN VARCHAR2) RETURN VARCHAR2 RESULT_CACHE;

  PROCEDURE assert(p_condition IN BOOLEAN, p_message IN VARCHAR2);
  PRAGMA RESTRICT_REFERENCES(mask_card_no, WNDS, WNPS);

END card_types;
/

CREATE OR REPLACE PACKAGE BODY card_types AS

  CURSOR c_active_cards(p_issuer_id IN NUMBER, p_since IN DATE DEFAULT SYSDATE - 30) RETURN cards%ROWTYPE IS
    SELECT *
      FROM cards c
     WHERE c.issuer_id = p_issuer_id
       AND c.status = c_status_active
       AND c.issued_at >= p_since;

  FUNCTION mask_card_no(p_card_no IN t_card_no) RETURN VARCHAR2 DETERMINISTIC IS
  BEGIN
    IF p_card_no IS NULL OR LENGTH(p_card_no) < 10 THEN
      RETURN p_card_no;
    END IF;
    RETURN SUBSTR(p_card_no, 1, 6) || RPAD('*', LENGTH(p_card_no) - 10, '*') || SUBSTR(p_card_no, -4);
  END mask_card_no;

  FUNCTION is_valid_luhn(p_card_no IN t_card_no) RETURN BOOLEAN IS
    l_sum    PLS_INTEGER := 0;
    l_digit  PLS_INTEGER;
    l_double BOOLEAN := FALSE;
  BEGIN
    IF NOT REGEXP_LIKE(p_card_no, '^[0-9]{12,19}$') THEN
      RETURN FALSE;
    END IF;

    FOR i IN REVERSE 1 .. LENGTH(p_card_no) LOOP
      l_digit := TO_NUMBER(SUBSTR(p_card_no, i, 1));
      IF l_double THEN
        l_digit := l_digit * 2;
        IF l_digit > 9 THEN
          l_digit := l_digit - 9;
        END IF;
      END IF;
      l_sum := l_sum + l_digit;
      l_double := NOT l_double;
    END LOOP;

    RETURN MOD(l_sum, 10) = 0;
  END is_valid_luhn;

  FUNCTION to_minor_units(p_amount IN t_amount, p_ccy IN VARCHAR2 DEFAULT c_default_ccy)
    RETURN PLS_INTEGER
    DETERMINISTIC
    PARALLEL_ENABLE
  IS
    l_exponent PLS_INTEGER;
  BEGIN
    l_exponent := CASE UPPER(p_ccy)
                    WHEN 'JPY' THEN 0
                    WHEN 'KWD' THEN 3
                    ELSE 2
                  END;
    RETURN ROUND(p_amount * POWER(10, l_exponent));
  END to_minor_units;

  FUNCTION status_name(p_status IN VARCHAR2) RETURN VARCHAR2 RESULT_CACHE IS
    l_name VARCHAR2(30);
  BEGIN
    SELECT s.display_name
      INTO l_name
      FROM card_statuses s
     WHERE s.status_code = p_status;
    RETURN l_name;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      RETURN 'UNKNOWN (' || p_status || ')';
    WHEN TOO_MANY_ROWS THEN
      RAISE_APPLICATION_ERROR(-20099, 'Duplicate status code: ' || p_status, TRUE);
  END status_name;

  PROCEDURE assert(p_condition IN BOOLEAN, p_message IN VARCHAR2) IS
  BEGIN
    IF p_condition IS NULL OR NOT p_condition THEN
      RAISE_APPLICATION_ERROR(-20000, 'Assertion failed: ' || NVL(p_message, '(no message)'));
    END IF;
  END assert;

BEGIN
  -- Package initialization block.
  g_trace_enabled := SYS_CONTEXT('USERENV', 'CLIENT_IDENTIFIER') LIKE 'TRACE%';
EXCEPTION
  WHEN OTHERS THEN
    g_trace_enabled := FALSE;
END card_types;
/
