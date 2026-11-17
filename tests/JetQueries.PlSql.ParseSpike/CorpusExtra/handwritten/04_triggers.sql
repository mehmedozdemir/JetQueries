CREATE OR REPLACE TRIGGER cards_biu_trg
  BEFORE INSERT OR UPDATE OF card_no, status, balance ON cards
  FOR EACH ROW
  WHEN (NEW.status IS NOT NULL)
DECLARE
  l_user VARCHAR2(128) := SYS_CONTEXT('USERENV', 'SESSION_USER');
BEGIN
  IF INSERTING THEN
    IF :NEW.card_id IS NULL THEN
      :NEW.card_id := card_seq.NEXTVAL;
    END IF;
    :NEW.created_at := SYSTIMESTAMP;
    :NEW.created_by := l_user;
  ELSIF UPDATING('STATUS') AND :OLD.status = 'C' THEN
    RAISE_APPLICATION_ERROR(-20200, 'A closed card cannot change status');
  END IF;

  IF UPDATING AND :NEW.balance < 0 THEN
    RAISE_APPLICATION_ERROR(-20201, 'Negative balance on card ' || :OLD.card_id);
  END IF;

  :NEW.card_no := REPLACE(:NEW.card_no, ' ');
  :NEW.updated_at := SYSTIMESTAMP;
  :NEW.updated_by := l_user;
END cards_biu_trg;
/

CREATE OR REPLACE TRIGGER transactions_audit_trg
  FOR INSERT OR UPDATE OR DELETE ON transactions
  COMPOUND TRIGGER

  TYPE t_audit_tab IS TABLE OF transaction_audit%ROWTYPE INDEX BY PLS_INTEGER;
  g_rows t_audit_tab;

  PROCEDURE flush IS
  BEGIN
    FORALL i IN 1 .. g_rows.COUNT
      INSERT INTO transaction_audit VALUES g_rows(i);
    g_rows.DELETE;
  END flush;

  BEFORE STATEMENT IS
  BEGIN
    g_rows.DELETE;
  END BEFORE STATEMENT;

  AFTER EACH ROW IS
    l_index PLS_INTEGER := g_rows.COUNT + 1;
  BEGIN
    g_rows(l_index).audit_id := audit_seq.NEXTVAL;
    g_rows(l_index).txn_id := COALESCE(:NEW.txn_id, :OLD.txn_id);
    g_rows(l_index).action := CASE
                                WHEN INSERTING THEN 'I'
                                WHEN UPDATING THEN 'U'
                                ELSE 'D'
                              END;
    g_rows(l_index).old_amount := :OLD.amount;
    g_rows(l_index).new_amount := :NEW.amount;
    g_rows(l_index).changed_at := SYSTIMESTAMP;

    IF g_rows.COUNT >= 500 THEN
      flush;
    END IF;
  END AFTER EACH ROW;

  AFTER STATEMENT IS
  BEGIN
    flush;
  END AFTER STATEMENT;

END transactions_audit_trg;
/

CREATE OR REPLACE TRIGGER card_summary_ioi_trg
  INSTEAD OF INSERT ON card_summary_v
  FOR EACH ROW
BEGIN
  INSERT INTO card_holders (holder_id, full_name)
  VALUES (holder_seq.NEXTVAL, :NEW.holder_name);

  INSERT INTO cards (card_no, holder_id, status)
  VALUES (:NEW.card_no, holder_seq.CURRVAL, 'A');
END;
/

CREATE OR REPLACE TRIGGER schema_ddl_audit_trg
  AFTER DDL ON SCHEMA
DECLARE
  PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
  INSERT INTO ddl_audit (event, object_type, object_name, executed_by, executed_at)
  VALUES (ORA_SYSEVENT, ORA_DICT_OBJ_TYPE, ORA_DICT_OBJ_NAME, ORA_LOGIN_USER, SYSDATE);
  COMMIT;
END;
/

CREATE OR REPLACE TRIGGER logon_ctx_trg
  AFTER LOGON ON DATABASE
BEGIN
  EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_DATE_FORMAT = ''YYYY-MM-DD HH24:MI:SS''';
  DBMS_SESSION.SET_IDENTIFIER(SYS_CONTEXT('USERENV', 'OS_USER'));
END;
/

ALTER TRIGGER cards_biu_trg ENABLE;
ALTER TRIGGER schema_ddl_audit_trg DISABLE;
