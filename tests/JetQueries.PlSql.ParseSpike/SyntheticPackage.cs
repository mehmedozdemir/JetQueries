using System.Globalization;
using System.Text;

namespace JetQueries.PlSql.ParseSpike;

/// <summary>
/// Generates a package body of a chosen size from hand-written, realistic PL/SQL (cursors, bulk operations,
/// dynamic SQL, MERGE, exception handlers). It stands in for a large production package, which the public
/// grammar test files do not contain.
/// </summary>
internal static class SyntheticPackage
{
    private const string Header = """
        CREATE OR REPLACE PACKAGE BODY billing_pkg AS

          g_batch_size CONSTANT PLS_INTEGER := 500;

          PROCEDURE log_event(p_code IN VARCHAR2, p_message IN VARCHAR2) IS
            PRAGMA AUTONOMOUS_TRANSACTION;
          BEGIN
            INSERT INTO event_log (id, code, message, created_at)
            VALUES (event_log_seq.NEXTVAL, p_code, SUBSTR(p_message, 1, 4000), SYSTIMESTAMP);
            COMMIT;
          EXCEPTION
            WHEN OTHERS THEN
              ROLLBACK;
          END log_event;

        """;

    private const string Members = """
          FUNCTION get_customer_balance_#(p_customer_id IN customers.id%TYPE, p_as_of IN DATE DEFAULT SYSDATE) RETURN NUMBER IS
            l_balance NUMBER(18, 2) := 0;
          BEGIN
            SELECT NVL(SUM(CASE WHEN t.direction = 'C' THEN t.amount ELSE -t.amount END), 0)
              INTO l_balance
              FROM transactions t
              JOIN accounts a ON a.id = t.account_id
             WHERE a.customer_id = p_customer_id
               AND t.posted_at <= p_as_of
               AND t.status IN ('POSTED', 'SETTLED');
            RETURN l_balance;
          EXCEPTION
            WHEN NO_DATA_FOUND THEN
              RETURN 0;
          END get_customer_balance_#;

          PROCEDURE settle_batch_#(p_batch_id IN NUMBER, p_dry_run IN BOOLEAN DEFAULT FALSE) IS
            CURSOR c_items IS
              SELECT i.id, i.account_id, i.amount, i.currency
                FROM batch_items i
               WHERE i.batch_id = p_batch_id
                 AND i.status = 'PENDING'
               ORDER BY i.id
                 FOR UPDATE OF i.status SKIP LOCKED;
            TYPE t_ids IS TABLE OF batch_items.id%TYPE INDEX BY PLS_INTEGER;
            l_ids t_ids;
            l_count PLS_INTEGER := 0;
            l_total NUMBER := 0;
            e_limit EXCEPTION;
          BEGIN
            FOR r IN c_items LOOP
              l_count := l_count + 1;
              l_total := l_total + r.amount;
              l_ids(l_count) := r.id;
              IF r.amount < 0 THEN
                log_event('NEG', 'Negative amount for item ' || TO_CHAR(r.id));
                CONTINUE;
              ELSIF r.amount > 1000000 THEN
                RAISE e_limit;
              END IF;
              IF NOT p_dry_run THEN
                UPDATE accounts a
                   SET a.balance = a.balance + r.amount,
                       a.updated_at = SYSDATE
                 WHERE a.id = r.account_id;
              END IF;
              EXIT WHEN l_count >= g_batch_size;
            END LOOP;

            IF l_count > 0 AND NOT p_dry_run THEN
              FORALL idx IN 1 .. l_count
                UPDATE batch_items SET status = 'SETTLED', settled_at = SYSDATE WHERE id = l_ids(idx);
              MERGE INTO batch_totals bt
              USING (SELECT p_batch_id AS batch_id, l_total AS total FROM dual) s
                 ON (bt.batch_id = s.batch_id)
               WHEN MATCHED THEN UPDATE SET bt.total = bt.total + s.total
               WHEN NOT MATCHED THEN INSERT (batch_id, total) VALUES (s.batch_id, s.total);
            END IF;

            CASE
              WHEN l_total = 0 THEN log_event('EMPTY', 'Batch ' || p_batch_id || ' had nothing to settle');
              WHEN l_total > 0 THEN log_event('OK', 'Settled ' || l_count || ' items');
              ELSE NULL;
            END CASE;
          EXCEPTION
            WHEN e_limit THEN
              log_event('LIMIT', 'Amount limit exceeded in batch ' || p_batch_id);
              RAISE_APPLICATION_ERROR(-20001, 'Amount limit exceeded');
            WHEN OTHERS THEN
              log_event('ERR', SQLERRM);
              RAISE;
          END settle_batch_#;

          PROCEDURE archive_old_rows_#(p_table IN VARCHAR2, p_days IN PLS_INTEGER) IS
            TYPE t_rowids IS TABLE OF ROWID;
            l_rowids t_rowids;
            l_cursor SYS_REFCURSOR;
            l_sql VARCHAR2(4000);
            l_done BOOLEAN := FALSE;
          BEGIN
            -- Identifiers cannot be bound, so the table name is validated instead.
            l_sql := 'SELECT rowid FROM ' || DBMS_ASSERT.SQL_OBJECT_NAME(p_table) || ' WHERE created_at < :cutoff';
            OPEN l_cursor FOR l_sql USING TRUNC(SYSDATE) - p_days;
            WHILE NOT l_done LOOP
              FETCH l_cursor BULK COLLECT INTO l_rowids LIMIT g_batch_size;
              l_done := l_rowids.COUNT = 0;
              IF NOT l_done THEN
                BEGIN
                  FORALL idx IN 1 .. l_rowids.COUNT
                    EXECUTE IMMEDIATE 'DELETE FROM ' || DBMS_ASSERT.SQL_OBJECT_NAME(p_table) || ' WHERE rowid = :1' USING l_rowids(idx);
                  COMMIT;
                EXCEPTION
                  WHEN DUP_VAL_ON_INDEX THEN
                    NULL;
                END;
              END IF;
            END LOOP;
            CLOSE l_cursor;
          END archive_old_rows_#;

        """;

    private const string Footer = """
        END billing_pkg;
        /
        """;

    /// <summary>Generates a package body with at least <paramref name="minimumLines"/> lines.</summary>
    public static string Generate(int minimumLines)
    {
        var text = new StringBuilder(Header);
        var lines = CountLines(Header) + CountLines(Footer);
        var membersLines = CountLines(Members);
        for (var i = 1; lines < minimumLines; i++)
        {
            text.Append(Members.Replace("#", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            lines += membersLines;
        }

        return text.Append(Footer).ToString();
    }

    public static int CountLines(string text) => text.AsSpan().Count('\n') + (text.EndsWith('\n') ? 0 : 1);
}
