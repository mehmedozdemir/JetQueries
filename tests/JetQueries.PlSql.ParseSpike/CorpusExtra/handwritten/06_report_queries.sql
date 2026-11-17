-- Reporting queries: analytics, hierarchies, pivots, CTEs, JSON, set operators.

WITH daily AS (
  SELECT TRUNC(t.created_at) AS txn_day,
         t.card_id,
         SUM(CASE WHEN t.txn_type = 'DEBIT' THEN t.amount ELSE 0 END) AS debit_total,
         SUM(DECODE(t.txn_type, 'TOPUP', t.amount, 0)) AS topup_total,
         COUNT(*) AS txn_count
    FROM transactions t
   WHERE t.created_at >= ADD_MONTHS(TRUNC(SYSDATE, 'MM'), -3)
   GROUP BY TRUNC(t.created_at), t.card_id
  HAVING COUNT(*) > 1
),
ranked AS (
  SELECT d.*,
         RANK() OVER (PARTITION BY d.txn_day ORDER BY d.debit_total DESC) AS day_rank,
         SUM(d.debit_total) OVER (PARTITION BY d.card_id ORDER BY d.txn_day
                                  ROWS BETWEEN 6 PRECEDING AND CURRENT ROW) AS debit_7d,
         LAG(d.debit_total, 1, 0) OVER (PARTITION BY d.card_id ORDER BY d.txn_day) AS prev_debit,
         RATIO_TO_REPORT(d.debit_total) OVER (PARTITION BY d.txn_day) AS share_of_day,
         FIRST_VALUE(d.txn_day) IGNORE NULLS OVER (PARTITION BY d.card_id ORDER BY d.txn_day) AS first_day
    FROM daily d
)
SELECT r.txn_day,
       r.card_id,
       r.debit_total,
       ROUND(r.share_of_day * 100, 2) AS pct,
       r.debit_7d,
       r.debit_total - r.prev_debit AS delta
  FROM ranked r
 WHERE r.day_rank <= 10
 ORDER BY r.txn_day DESC NULLS LAST, r.day_rank
 OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY;

SELECT LPAD(' ', 2 * (LEVEL - 1)) || o.unit_name AS unit_tree,
       SYS_CONNECT_BY_PATH(o.unit_code, '/') AS unit_path,
       CONNECT_BY_ROOT o.unit_name AS root_unit,
       CONNECT_BY_ISLEAF AS is_leaf,
       LEVEL AS depth
  FROM org_units o
 START WITH o.parent_id IS NULL
CONNECT BY NOCYCLE PRIOR o.unit_id = o.parent_id
 ORDER SIBLINGS BY o.unit_name;

SELECT *
  FROM (SELECT t.channel, TO_CHAR(t.created_at, 'YYYY-MM') AS month, t.amount FROM topups t)
 PIVOT (SUM(amount) AS total, COUNT(*) AS cnt FOR channel IN ('WEB' AS web, 'POS' AS pos, 'ATM' AS atm))
 ORDER BY month;

SELECT card_id, quarter, amount
  FROM quarterly_totals
UNPIVOT INCLUDE NULLS (amount FOR quarter IN (q1 AS 'Q1', q2 AS 'Q2', q3 AS 'Q3', q4 AS 'Q4'));

SELECT h.holder_id,
       h.full_name,
       LISTAGG(c.card_no, ', ') WITHIN GROUP (ORDER BY c.issued_at) AS cards,
       MAX(c.balance) KEEP (DENSE_RANK LAST ORDER BY c.issued_at) AS newest_balance,
       COUNT(DISTINCT c.status) AS statuses
  FROM card_holders h
  LEFT OUTER JOIN cards c ON c.holder_id = h.holder_id AND c.status IN ('A', 'B')
 WHERE h.created_at BETWEEN DATE '2025-01-01' AND TIMESTAMP '2025-12-31 23:59:59'
   AND (h.email LIKE '%@example.com' OR h.phone IS NOT NULL)
   AND NOT EXISTS (SELECT NULL FROM blacklist b WHERE b.holder_id = h.holder_id)
 GROUP BY ROLLUP (h.holder_id, h.full_name)
 ORDER BY 2;

SELECT c.card_id, x.last_txn_at, x.last_amount
  FROM cards c
 CROSS APPLY (SELECT t.created_at AS last_txn_at, t.amount AS last_amount
                FROM transactions t
               WHERE t.card_id = c.card_id
               ORDER BY t.created_at DESC
               FETCH FIRST 1 ROW ONLY) x
 WHERE c.status = 'A';

SELECT c.card_id, c.balance, t.cnt
  FROM cards c,
       LATERAL (SELECT COUNT(*) AS cnt FROM transactions tx WHERE tx.card_id = c.card_id) t
 WHERE c.issuer_id = :issuer_id
   AND c.card_no = :card_no;

SELECT e.event_id, j.device_id, j.reader, j.lat, j.lon
  FROM gate_events e,
       JSON_TABLE(e.payload, '$'
         COLUMNS (
           device_id VARCHAR2(40) PATH '$.device.id',
           reader    VARCHAR2(20) PATH '$.device.reader',
           lat       NUMBER       PATH '$.location.lat',
           lon       NUMBER       PATH '$.location.lon',
           NESTED PATH '$.tags[*]' COLUMNS (tag VARCHAR2(30) PATH '$')
         )) j
 WHERE JSON_EXISTS(e.payload, '$.device.id')
   AND JSON_VALUE(e.payload, '$.type' RETURNING VARCHAR2(20)) = 'ENTRY';

SELECT JSON_OBJECT(
         'cardId' VALUE c.card_id,
         'holder' VALUE h.full_name,
         'balance' VALUE c.balance,
         'txns' VALUE (SELECT JSON_ARRAYAGG(JSON_OBJECT('id' VALUE t.txn_id, 'amount' VALUE t.amount)
                                           ORDER BY t.created_at RETURNING CLOB)
                         FROM transactions t
                        WHERE t.card_id = c.card_id)
         ABSENT ON NULL) AS doc
  FROM cards c
  JOIN card_holders h USING (holder_id);

SELECT card_id FROM cards WHERE status = 'A'
MINUS
SELECT card_id FROM transactions WHERE created_at > SYSDATE - 90
UNION ALL
SELECT card_id FROM refund_queue
INTERSECT
SELECT card_id FROM cards WHERE balance > 0;

SELECT XMLELEMENT("card",
         XMLATTRIBUTES(c.card_id AS "id"),
         XMLFOREST(c.card_no AS "number", c.balance AS "balance"),
         XMLAGG(XMLELEMENT("txn", t.amount) ORDER BY t.created_at)) AS card_xml
  FROM cards c
  JOIN transactions t ON t.card_id = c.card_id
 GROUP BY c.card_id, c.card_no, c.balance;

SELECT /*+ PARALLEL(t 4) INDEX(c cards_pk) */
       c.issuer_id,
       EXTRACT(YEAR FROM t.created_at) AS yr,
       MEDIAN(t.amount) AS median_amount,
       PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY t.amount) AS p95,
       STDDEV(t.amount) AS sd,
       GROUPING(c.issuer_id) AS is_total
  FROM transactions PARTITION (p_2025) t
  JOIN cards c ON c.card_id = t.card_id
 GROUP BY CUBE (c.issuer_id, EXTRACT(YEAR FROM t.created_at));

MERGE INTO card_daily_stats s
USING (SELECT card_id, TRUNC(created_at) AS txn_day, SUM(amount) AS total, COUNT(*) AS cnt
         FROM transactions
        WHERE created_at >= TRUNC(SYSDATE) - 1
        GROUP BY card_id, TRUNC(created_at)) d
   ON (s.card_id = d.card_id AND s.txn_day = d.txn_day)
 WHEN MATCHED THEN
   UPDATE SET s.total = d.total, s.cnt = d.cnt, s.updated_at = SYSDATE
    WHERE s.total <> d.total
   DELETE WHERE s.cnt = 0
 WHEN NOT MATCHED THEN
   INSERT (card_id, txn_day, total, cnt, updated_at)
   VALUES (d.card_id, d.txn_day, d.total, d.cnt, SYSDATE)
    WHERE d.cnt > 0;

INSERT ALL
  WHEN amount >= 1000 THEN INTO large_txns (txn_id, amount) VALUES (txn_id, amount)
  WHEN amount < 0 THEN INTO suspect_txns (txn_id, amount, reason) VALUES (txn_id, amount, 'NEGATIVE')
  ELSE INTO normal_txns (txn_id, amount) VALUES (txn_id, amount)
SELECT txn_id, amount FROM transactions WHERE created_at >= TRUNC(SYSDATE);

UPDATE (SELECT c.balance, s.total
          FROM cards c
          JOIN card_daily_stats s ON s.card_id = c.card_id
         WHERE s.txn_day = TRUNC(SYSDATE)) v
   SET v.balance = v.balance - v.total;

DELETE FROM transactions t
 WHERE t.created_at < ADD_MONTHS(SYSDATE, -84)
   AND t.card_id IN (SELECT c.card_id FROM cards c WHERE c.status = 'C')
RETURNING t.txn_id BULK COLLECT INTO :deleted_ids;

SELECT * FROM transactions AS OF TIMESTAMP (SYSTIMESTAMP - INTERVAL '15' MINUTE) WHERE card_id = 42;

SELECT t.*, ROWNUM AS rn FROM transactions SAMPLE (5) t WHERE ROWNUM <= 1000 FOR UPDATE SKIP LOCKED;
