CREATE SEQUENCE card_seq START WITH 1000 INCREMENT BY 1 NOCACHE NOCYCLE;
CREATE SEQUENCE txn_seq MINVALUE 1 MAXVALUE 999999999999 CACHE 1000 ORDER;

CREATE TABLE card_holders (
  holder_id   NUMBER(18) NOT NULL,
  full_name   VARCHAR2(200 CHAR) NOT NULL,
  national_id VARCHAR2(11),
  email       VARCHAR2(320),
  phone       VARCHAR2(20),
  birth_date  DATE,
  created_at  TIMESTAMP(6) DEFAULT SYSTIMESTAMP NOT NULL,
  CONSTRAINT card_holders_pk PRIMARY KEY (holder_id),
  CONSTRAINT card_holders_nid_uk UNIQUE (national_id),
  CONSTRAINT card_holders_email_ck CHECK (email IS NULL OR REGEXP_LIKE(email, '^[^@]+@[^@]+\.[^@]+$'))
) TABLESPACE users PCTFREE 10;

CREATE TABLE cards (
  card_id      NUMBER(18) NOT NULL,
  card_no      VARCHAR2(19) NOT NULL,
  holder_id    NUMBER(18) NOT NULL,
  issuer_id    NUMBER(9),
  status       CHAR(1) DEFAULT 'A' NOT NULL,
  balance      NUMBER(12, 2) DEFAULT 0 NOT NULL,
  issued_at    DATE DEFAULT SYSDATE NOT NULL,
  expires_at   DATE,
  last_used_at TIMESTAMP,
  usage_count  NUMBER(10),
  masked_no    VARCHAR2(19) GENERATED ALWAYS AS (SUBSTR(card_no, 1, 6) || '******' || SUBSTR(card_no, -4)) VIRTUAL,
  CONSTRAINT cards_pk PRIMARY KEY (card_id) USING INDEX TABLESPACE users_idx,
  CONSTRAINT cards_no_uk UNIQUE (card_no),
  CONSTRAINT cards_holder_fk FOREIGN KEY (holder_id) REFERENCES card_holders (holder_id) ON DELETE CASCADE,
  CONSTRAINT cards_status_ck CHECK (status IN ('A', 'B', 'C')),
  CONSTRAINT cards_balance_ck CHECK (balance >= 0) DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE transactions (
  txn_id        NUMBER(18) NOT NULL,
  card_id       NUMBER(18) NOT NULL,
  txn_type      VARCHAR2(10) NOT NULL,
  amount        NUMBER(12, 2) NOT NULL,
  balance_after NUMBER(12, 2),
  reference_no  VARCHAR2(64),
  created_at    TIMESTAMP(6) NOT NULL,
  created_by    VARCHAR2(128),
  CONSTRAINT transactions_pk PRIMARY KEY (txn_id, created_at)
)
PARTITION BY RANGE (created_at) INTERVAL (NUMTOYMINTERVAL(1, 'MONTH'))
SUBPARTITION BY HASH (card_id) SUBPARTITIONS 8
(
  PARTITION p_2025 VALUES LESS THAN (TIMESTAMP '2026-01-01 00:00:00'),
  PARTITION p_2026_01 VALUES LESS THAN (TIMESTAMP '2026-02-01 00:00:00')
)
COMPRESS FOR OLTP
ENABLE ROW MOVEMENT;

CREATE GLOBAL TEMPORARY TABLE settlement_stage_tmp (
  stage_id NUMBER,
  card_id  NUMBER,
  amount   NUMBER
) ON COMMIT PRESERVE ROWS;

CREATE TABLE cards_backup AS SELECT * FROM cards WHERE 1 = 0;

CREATE INDEX cards_holder_ix ON cards (holder_id) TABLESPACE users_idx;
CREATE UNIQUE INDEX card_holders_email_ux ON card_holders (LOWER(email));
CREATE BITMAP INDEX cards_status_bx ON cards (status);
CREATE INDEX transactions_card_ix ON transactions (card_id, created_at DESC) LOCAL COMPRESS 1 ONLINE;

ALTER TABLE cards ADD (closed_at DATE, note VARCHAR2(500));
ALTER TABLE cards MODIFY (note VARCHAR2(1000) DEFAULT NULL);
ALTER TABLE cards RENAME COLUMN note TO remarks;
ALTER TABLE cards DROP COLUMN remarks;
ALTER TABLE cards ADD CONSTRAINT cards_dates_ck CHECK (expires_at IS NULL OR expires_at > issued_at) ENABLE NOVALIDATE;
ALTER TABLE cards DISABLE CONSTRAINT cards_balance_ck;
ALTER TABLE transactions ADD PARTITION p_2026_02 VALUES LESS THAN (TIMESTAMP '2026-03-01 00:00:00');
ALTER TABLE transactions TRUNCATE PARTITION p_2025 UPDATE GLOBAL INDEXES;
ALTER TABLE transactions MOVE PARTITION p_2026_01 TABLESPACE users COMPRESS;
ALTER INDEX cards_holder_ix REBUILD ONLINE;
ALTER SEQUENCE card_seq INCREMENT BY 10;

CREATE MATERIALIZED VIEW card_balances_mv
  BUILD IMMEDIATE
  REFRESH FAST ON DEMAND
  ENABLE QUERY REWRITE
AS
SELECT c.issuer_id, c.status, COUNT(*) AS card_count, SUM(c.balance) AS total_balance
  FROM cards c
 GROUP BY c.issuer_id, c.status;

CREATE MATERIALIZED VIEW LOG ON cards WITH ROWID, SEQUENCE (issuer_id, status, balance) INCLUDING NEW VALUES;

CREATE OR REPLACE SYNONYM crd FOR cards;
CREATE PUBLIC SYNONYM card_api FOR app.card_ops;
CREATE DATABASE LINK reporting_db CONNECT TO report_user IDENTIFIED BY "not-a-real-password" USING 'REPORTING';
CREATE OR REPLACE DIRECTORY export_dir AS '/data/export';
CREATE OR REPLACE CONTEXT app_ctx USING app.ctx_pkg;

COMMENT ON TABLE cards IS 'Issued cards; one row per physical or virtual card';
COMMENT ON COLUMN cards.status IS 'A = active, B = blocked, C = closed';

GRANT SELECT, INSERT, UPDATE ON cards TO app_role;
GRANT EXECUTE ON card_ops TO app_role WITH GRANT OPTION;
GRANT SELECT ON card_summary_v TO PUBLIC;
REVOKE UPDATE ON cards FROM app_role;
CREATE ROLE report_role;
GRANT CREATE SESSION, CREATE TABLE TO report_user;

RENAME cards_backup TO cards_backup_old;
TRUNCATE TABLE cards_backup_old;
DROP TABLE cards_backup_old CASCADE CONSTRAINTS PURGE;
DROP INDEX cards_status_bx;
DROP SEQUENCE txn_seq;
DROP PACKAGE BODY card_ops;
DROP TYPE t_money FORCE;
DROP MATERIALIZED VIEW card_balances_mv;
ANALYZE TABLE cards COMPUTE STATISTICS;
FLASHBACK TABLE cards TO BEFORE DROP;
PURGE RECYCLEBIN;

SELECT c.card_no, r.total FROM cards c JOIN report_totals@reporting_db r ON r.card_id = c.card_id;
