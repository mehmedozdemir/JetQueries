CREATE OR REPLACE PACKAGE BODY card_ops AS

  gc_module CONSTANT VARCHAR2(30) := 'CARD_OPS';

  TYPE t_balance_cache IS TABLE OF card_types.t_amount INDEX BY PLS_INTEGER;
  g_cache t_balance_cache;

  -- Forward declarations.
  PROCEDURE trace(p_text IN VARCHAR2);
  FUNCTION lock_card(p_card_id IN cards.card_id%TYPE) RETURN cards%ROWTYPE;

  PROCEDURE trace(p_text IN VARCHAR2) IS
  BEGIN
    IF card_types.g_trace_enabled THEN
      DBMS_OUTPUT.PUT_LINE(TO_CHAR(SYSTIMESTAMP, 'HH24:MI:SS.FF3') || ' [' || gc_module || '] ' || p_text);
      DBMS_APPLICATION_INFO.SET_ACTION(SUBSTR(p_text, 1, 64));
    END IF;
  END trace;

  FUNCTION lock_card(p_card_id IN cards.card_id%TYPE) RETURN cards%ROWTYPE IS
    l_card cards%ROWTYPE;
    e_row_locked EXCEPTION;
    PRAGMA EXCEPTION_INIT(e_row_locked, -54);
  BEGIN
    SELECT *
      INTO l_card
      FROM cards
     WHERE card_id = p_card_id
       FOR UPDATE WAIT 5;
    RETURN l_card;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      RAISE card_types.e_card_not_found;
    WHEN e_row_locked THEN
      RAISE_APPLICATION_ERROR(-20020, 'Card ' || p_card_id || ' is busy, try again');
  END lock_card;

  FUNCTION get_balance(p_card_id IN cards.card_id%TYPE, p_use_cache IN BOOLEAN DEFAULT TRUE)
    RETURN card_types.t_amount
  IS
    l_balance card_types.t_amount;
  BEGIN
    IF p_use_cache AND g_cache.EXISTS(p_card_id) THEN
      RETURN g_cache(p_card_id);
    END IF;

    SELECT c.balance
      INTO l_balance
      FROM cards c
     WHERE c.card_id = p_card_id;

    g_cache(p_card_id) := l_balance;
    RETURN l_balance;
  EXCEPTION
    WHEN NO_DATA_FOUND THEN
      RETURN NULL;
  END get_balance;

  PROCEDURE debit(
    p_card_id     IN  cards.card_id%TYPE,
    p_amount      IN  card_types.t_amount,
    p_reference   IN  VARCHAR2,
    p_new_balance OUT NOCOPY card_types.t_amount,
    p_txn_id      OUT transactions.txn_id%TYPE
  ) IS
    l_card cards%ROWTYPE;
  BEGIN
    card_types.assert(p_amount > 0, 'Amount must be positive');
    SAVEPOINT before_debit;

    l_card := lock_card(p_card_id);

    CASE l_card.status
      WHEN card_types.c_status_blocked THEN
        RAISE card_types.e_card_blocked;
      WHEN card_types.c_status_closed THEN
        RAISE_APPLICATION_ERROR(-20012, 'Card is closed');
      ELSE
        NULL;
    END CASE;

    IF l_card.balance < p_amount THEN
      RAISE card_types.e_insufficient_balance;
    END IF;

    UPDATE cards c
       SET c.balance = c.balance - p_amount,
           c.last_used_at = SYSTIMESTAMP,
           c.usage_count = NVL(c.usage_count, 0) + 1
     WHERE c.card_id = p_card_id
    RETURNING c.balance INTO p_new_balance;

    INSERT INTO transactions (txn_id, card_id, txn_type, amount, balance_after, reference_no, created_at, created_by)
    VALUES (txn_seq.NEXTVAL, p_card_id, 'DEBIT', p_amount, p_new_balance, p_reference, SYSTIMESTAMP, USER)
    RETURNING txn_id INTO p_txn_id;

    g_cache.DELETE(p_card_id);
    trace('debit ' || p_card_id || ' amount=' || TO_CHAR(p_amount, 'FM999G990D00'));
  EXCEPTION
    WHEN card_types.e_insufficient_balance OR card_types.e_card_blocked THEN
      ROLLBACK TO SAVEPOINT before_debit;
      RAISE;
    WHEN OTHERS THEN
      ROLLBACK TO before_debit;
      trace('debit failed: ' || SQLERRM || CHR(10) || DBMS_UTILITY.FORMAT_ERROR_BACKTRACE);
      RAISE;
  END debit;

  PROCEDURE transfer(
    p_from_card IN cards.card_id%TYPE,
    p_to_card   IN cards.card_id%TYPE,
    p_amount    IN card_types.t_amount
  ) IS
    l_first   cards.card_id%TYPE := LEAST(p_from_card, p_to_card);
    l_second  cards.card_id%TYPE := GREATEST(p_from_card, p_to_card);
    l_dummy   cards%ROWTYPE;
    l_balance card_types.t_amount;
    l_txn_id  transactions.txn_id%TYPE;
  BEGIN
    IF p_from_card = p_to_card THEN
      RETURN;
    END IF;

    -- Always lock in the same order to avoid deadlocks.
    l_dummy := lock_card(l_first);
    l_dummy := lock_card(l_second);

    debit(
      p_card_id     => p_from_card,
      p_amount      => p_amount,
      p_reference   => 'TRF>' || p_to_card,
      p_new_balance => l_balance,
      p_txn_id      => l_txn_id);

    UPDATE cards
       SET balance = balance + p_amount
     WHERE card_id = p_to_card
       AND balance + p_amount <= card_types.c_max_balance;

    IF SQL%ROWCOUNT = 0 THEN
      RAISE_APPLICATION_ERROR(-20013, 'Target card would exceed the maximum balance');
    END IF;

    COMMIT;
  END transfer;

  FUNCTION find_cards(p_holder_like IN VARCHAR2, p_max_rows IN PLS_INTEGER DEFAULT 100)
    RETURN card_types.t_card_tab
  IS
    l_result card_types.t_card_tab := card_types.t_card_tab();
  BEGIN
    FOR r IN (
      SELECT c.card_id, c.card_no, c.balance, c.status, c.issued_at, h.full_name
        FROM cards c
        JOIN card_holders h ON h.holder_id = c.holder_id
       WHERE UPPER(h.full_name) LIKE UPPER(p_holder_like) ESCAPE '\'
       ORDER BY h.full_name, c.issued_at DESC
       FETCH FIRST p_max_rows ROWS ONLY
    ) LOOP
      l_result.EXTEND;
      l_result(l_result.LAST).card_id := r.card_id;
      l_result(l_result.LAST).card_no := card_types.mask_card_no(r.card_no);
      l_result(l_result.LAST).balance := r.balance;
      l_result(l_result.LAST).status := r.status;
      l_result(l_result.LAST).issued_at := r.issued_at;
      l_result(l_result.LAST).holder := r.full_name;
    END LOOP;
    RETURN l_result;
  END find_cards;

  PROCEDURE close_expired(p_as_of IN DATE DEFAULT TRUNC(SYSDATE), p_closed OUT PLS_INTEGER) IS
    CURSOR c_expired IS
      SELECT c.ROWID AS rid, c.card_id, c.balance
        FROM cards c
       WHERE c.expires_at < p_as_of
         AND c.status <> card_types.c_status_closed
         FOR UPDATE OF c.status NOWAIT;
    l_row c_expired%ROWTYPE;
  BEGIN
    p_closed := 0;
    OPEN c_expired;
    <<expired_loop>>
    LOOP
      FETCH c_expired INTO l_row;
      EXIT expired_loop WHEN c_expired%NOTFOUND;

      IF l_row.balance > 0 THEN
        INSERT INTO refund_queue (card_id, amount, queued_at)
        VALUES (l_row.card_id, l_row.balance, SYSDATE);
      END IF;

      UPDATE cards
         SET status = card_types.c_status_closed,
             balance = 0,
             closed_at = SYSDATE
       WHERE CURRENT OF c_expired;

      p_closed := p_closed + 1;
    END LOOP expired_loop;
    CLOSE c_expired;
  EXCEPTION
    WHEN OTHERS THEN
      IF c_expired%ISOPEN THEN
        CLOSE c_expired;
      END IF;
      RAISE;
  END close_expired;

  PROCEDURE reset_cache IS
  BEGIN
    g_cache.DELETE;
  END reset_cache;

END card_ops;
/
