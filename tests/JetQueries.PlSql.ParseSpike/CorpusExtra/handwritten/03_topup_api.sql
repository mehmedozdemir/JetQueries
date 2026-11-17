CREATE OR REPLACE PACKAGE topup_api AUTHID CURRENT_USER AS

  TYPE t_topup_row IS RECORD (
    topup_id   NUMBER,
    card_no    VARCHAR2(19),
    amount     NUMBER,
    channel    VARCHAR2(20),
    created_at TIMESTAMP WITH TIME ZONE
  );
  TYPE t_topup_tab IS TABLE OF t_topup_row;

  -- Overloads: by card id, by card number, and in bulk.
  PROCEDURE topup(p_card_id IN NUMBER, p_amount IN NUMBER, p_channel IN VARCHAR2 DEFAULT 'WEB');
  PROCEDURE topup(p_card_no IN VARCHAR2, p_amount IN NUMBER, p_channel IN VARCHAR2 DEFAULT 'WEB');
  PROCEDURE topup(p_requests IN SYS.ODCINUMBERLIST, p_amount IN NUMBER);

  FUNCTION recent_topups(p_card_id IN NUMBER, p_days IN PLS_INTEGER DEFAULT 7)
    RETURN t_topup_tab PIPELINED;

  FUNCTION daily_total(p_day IN DATE, p_channel IN VARCHAR2 DEFAULT NULL)
    RETURN NUMBER RESULT_CACHE;

  FUNCTION open_topups(p_card_id IN NUMBER) RETURN SYS_REFCURSOR;

END topup_api;
/

CREATE OR REPLACE PACKAGE BODY topup_api AS

  c_min_amount CONSTANT NUMBER := 5;
  c_max_amount CONSTANT NUMBER := 2500;

  PROCEDURE validate(p_amount IN NUMBER) IS
  BEGIN
    IF p_amount IS NULL THEN
      RAISE_APPLICATION_ERROR(-20100, 'Amount is required');
    ELSIF p_amount NOT BETWEEN c_min_amount AND c_max_amount THEN
      RAISE_APPLICATION_ERROR(
        -20101,
        'Amount must be between ' || c_min_amount || ' and ' || c_max_amount);
    ELSIF MOD(p_amount * 100, 1) != 0 THEN
      RAISE_APPLICATION_ERROR(-20102, 'Amount has more than two decimals');
    END IF;
  END validate;

  PROCEDURE topup(p_card_id IN NUMBER, p_amount IN NUMBER, p_channel IN VARCHAR2 DEFAULT 'WEB') IS
    l_new_balance NUMBER;
  BEGIN
    validate(p_amount);

    UPDATE cards
       SET balance = balance + p_amount
     WHERE card_id = p_card_id
       AND status = 'A'
    RETURNING balance INTO l_new_balance;

    IF SQL%NOTFOUND THEN
      RAISE_APPLICATION_ERROR(-20103, 'Active card not found: ' || p_card_id);
    END IF;

    INSERT INTO topups (topup_id, card_id, amount, channel, balance_after, created_at)
    VALUES (topup_seq.NEXTVAL, p_card_id, p_amount, UPPER(TRIM(p_channel)), l_new_balance, SYSTIMESTAMP);
  END topup;

  PROCEDURE topup(p_card_no IN VARCHAR2, p_amount IN NUMBER, p_channel IN VARCHAR2 DEFAULT 'WEB') IS
    l_card_id cards.card_id%TYPE;
  BEGIN
    BEGIN
      SELECT card_id INTO l_card_id FROM cards WHERE card_no = REPLACE(p_card_no, ' ');
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        l_card_id := NULL;
    END;

    IF l_card_id IS NULL THEN
      RAISE_APPLICATION_ERROR(-20104, 'Unknown card number');
    END IF;

    topup(p_card_id => l_card_id, p_amount => p_amount, p_channel => p_channel);
  END topup;

  PROCEDURE topup(p_requests IN SYS.ODCINUMBERLIST, p_amount IN NUMBER) IS
  BEGIN
    validate(p_amount);
    IF p_requests IS NULL OR p_requests.COUNT = 0 THEN
      RETURN;
    END IF;

    FORALL i IN p_requests.FIRST .. p_requests.LAST
      UPDATE cards
         SET balance = balance + p_amount
       WHERE card_id = p_requests(i)
         AND status = 'A';

    INSERT INTO topups (topup_id, card_id, amount, channel, created_at)
    SELECT topup_seq.NEXTVAL, t.COLUMN_VALUE, p_amount, 'BULK', SYSTIMESTAMP
      FROM TABLE(p_requests) t
     WHERE EXISTS (SELECT 1 FROM cards c WHERE c.card_id = t.COLUMN_VALUE AND c.status = 'A');
  END topup;

  FUNCTION recent_topups(p_card_id IN NUMBER, p_days IN PLS_INTEGER DEFAULT 7)
    RETURN t_topup_tab PIPELINED
  IS
    l_row t_topup_row;
  BEGIN
    FOR r IN (
      SELECT t.topup_id, c.card_no, t.amount, t.channel, t.created_at
        FROM topups t
        JOIN cards c ON c.card_id = t.card_id
       WHERE t.card_id = p_card_id
         AND t.created_at >= SYSTIMESTAMP - NUMTODSINTERVAL(p_days, 'DAY')
       ORDER BY t.created_at DESC
    ) LOOP
      l_row.topup_id := r.topup_id;
      l_row.card_no := r.card_no;
      l_row.amount := r.amount;
      l_row.channel := r.channel;
      l_row.created_at := r.created_at;
      PIPE ROW (l_row);
    END LOOP;
    RETURN;
  EXCEPTION
    WHEN NO_DATA_NEEDED THEN
      NULL;
  END recent_topups;

  FUNCTION daily_total(p_day IN DATE, p_channel IN VARCHAR2 DEFAULT NULL)
    RETURN NUMBER RESULT_CACHE RELIES_ON (topups)
  IS
    l_total NUMBER;
  BEGIN
    SELECT COALESCE(SUM(t.amount), 0)
      INTO l_total
      FROM topups t
     WHERE t.created_at >= TRUNC(p_day)
       AND t.created_at < TRUNC(p_day) + 1
       AND (p_channel IS NULL OR t.channel = p_channel);
    RETURN l_total;
  END daily_total;

  FUNCTION open_topups(p_card_id IN NUMBER) RETURN SYS_REFCURSOR IS
    l_cursor SYS_REFCURSOR;
  BEGIN
    OPEN l_cursor FOR
      SELECT t.topup_id,
             t.amount,
             t.channel,
             TO_CHAR(t.created_at, 'YYYY-MM-DD"T"HH24:MI:SS') AS created_iso
        FROM topups t
       WHERE t.card_id = p_card_id
         AND t.settled = 'N'
       ORDER BY t.created_at;
    RETURN l_cursor;
  END open_topups;

END topup_api;
/

-- Callers
SELECT * FROM TABLE(topup_api.recent_topups(1001, 30)) WHERE amount > 50;

BEGIN
  topup_api.topup(p_card_no => '6032 1100 0000 1234', p_amount => 100);
  topup_api.topup(SYS.ODCINUMBERLIST(1001, 1002, 1003), 25);
  COMMIT;
END;
/
