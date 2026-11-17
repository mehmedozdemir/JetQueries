-- Oracle 12c - 23ai features. Expected to probe the edges of the grammar.

CREATE OR REPLACE PACKAGE modern_pkg
  AUTHID DEFINER
  ACCESSIBLE BY (PACKAGE card_ops, PROCEDURE purge_old_logs)
AS
  TYPE t_point IS RECORD (x NUMBER, y NUMBER);
  TYPE t_points IS TABLE OF t_point INDEX BY PLS_INTEGER;
  TYPE t_names IS TABLE OF VARCHAR2(30) INDEX BY VARCHAR2(10);

  FUNCTION origin RETURN t_point;
  FUNCTION top_n(p_table IN VARCHAR2, p_n IN NUMBER) RETURN VARCHAR2 SQL_MACRO;
  PROCEDURE old_api(p_id IN NUMBER);
  PRAGMA DEPRECATE(old_api, 'Use new_api instead');
END modern_pkg;
/

CREATE OR REPLACE PACKAGE BODY modern_pkg AS

  FUNCTION origin RETURN t_point IS
  BEGIN
    -- 18c qualified expressions for records and associative arrays.
    RETURN t_point(x => 0, y => 0);
  END origin;

  FUNCTION top_n(p_table IN VARCHAR2, p_n IN NUMBER) RETURN VARCHAR2 SQL_MACRO IS
  BEGIN
    RETURN q'[SELECT * FROM p_table FETCH FIRST p_n ROWS ONLY]';
  END top_n;

  PROCEDURE old_api(p_id IN NUMBER) IS
    l_points t_points := t_points(1 => t_point(1, 2), 2 => t_point(x => 3, y => 4));
    l_names  t_names := t_names('tr' => 'Türkiye', 'de' => 'Deutschland');
    l_doc    JSON_OBJECT_T := JSON_OBJECT_T.parse('{"id": 1, "tags": ["a", "b"]}');
    l_tags   JSON_ARRAY_T;
    l_sum    NUMBER := 0;
  BEGIN
    l_tags := l_doc.get_Array('tags');
    l_doc.put('count', l_tags.get_size);

    -- 21c iteration controls.
    FOR i IN 1 .. 10 BY 2 LOOP
      l_sum := l_sum + i;
    END LOOP;

    FOR i IN 1 .. 3, REVERSE 7 .. 9 LOOP
      l_sum := l_sum + i;
    END LOOP;

    FOR idx, pt IN PAIRS OF l_points LOOP
      l_sum := l_sum + idx + pt.x;
    END LOOP;

    FOR v IN VALUES OF l_names LOOP
      DBMS_OUTPUT.PUT_LINE(v);
    END LOOP;

    FOR i IN 1 .. 100 WHEN MOD(i, 7) = 0 LOOP
      l_sum := l_sum + i;
    END LOOP;

    DBMS_OUTPUT.PUT_LINE(l_doc.to_string || ' ' || l_sum || ' ' || p_id);
  END old_api;

END modern_pkg;
/

-- 12c: inline PL/SQL in a WITH clause.
WITH
  FUNCTION with_vat(p_amount IN NUMBER) RETURN NUMBER IS
  BEGIN
    RETURN ROUND(p_amount * 1.20, 2);
  END;
SELECT t.txn_id, t.amount, with_vat(t.amount) AS gross
  FROM transactions t
 WHERE ROWNUM <= 10
/

-- 12c: identity, invisible columns, default on null, row limiting, match_recognize.
CREATE TABLE gate_passes (
  pass_id     NUMBER GENERATED ALWAYS AS IDENTITY (START WITH 1 INCREMENT BY 1 CACHE 100),
  card_id     NUMBER NOT NULL,
  gate_code   VARCHAR2(10) DEFAULT ON NULL 'UNKNOWN',
  passed_at   TIMESTAMP DEFAULT SYSTIMESTAMP NOT NULL,
  note        VARCHAR2(4000) INVISIBLE,
  payload     CLOB CHECK (payload IS JSON),
  CONSTRAINT gate_passes_pk PRIMARY KEY (pass_id)
);

SELECT *
  FROM gate_passes
 MATCH_RECOGNIZE (
   PARTITION BY card_id
   ORDER BY passed_at
   MEASURES FIRST(a.passed_at) AS first_pass, LAST(b.passed_at) AS last_pass, COUNT(*) AS passes
   ONE ROW PER MATCH
   AFTER MATCH SKIP PAST LAST ROW
   PATTERN (a b{2,})
   DEFINE b AS b.passed_at <= PREV(b.passed_at) + INTERVAL '5' MINUTE
 );

SELECT card_id, balance FROM cards ORDER BY balance DESC FETCH FIRST 10 PERCENT ROWS WITH TIES;

-- 23ai: boolean type, IF [NOT] EXISTS, table value constructor, SELECT without FROM, GROUP BY alias.
CREATE TABLE IF NOT EXISTS feature_flags (
  flag_name VARCHAR2(50) PRIMARY KEY,
  enabled   BOOLEAN DEFAULT FALSE NOT NULL
);

INSERT INTO feature_flags (flag_name, enabled) VALUES ('new_grid', TRUE), ('plsql_parser', FALSE);

SELECT SYSDATE;

SELECT EXTRACT(YEAR FROM created_at) AS yr, COUNT(*) FROM transactions GROUP BY yr;

DROP TABLE IF EXISTS feature_flags_old;

UPDATE cards c
   SET c.status = 'B'
  FROM blacklist b
 WHERE b.holder_id = c.holder_id;
