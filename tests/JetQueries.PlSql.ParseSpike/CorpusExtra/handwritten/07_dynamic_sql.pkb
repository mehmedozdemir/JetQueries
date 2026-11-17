CREATE OR REPLACE PACKAGE BODY dyn_report AS

  FUNCTION quote_name(p_name IN VARCHAR2) RETURN VARCHAR2 IS
  BEGIN
    RETURN DBMS_ASSERT.ENQUOTE_NAME(DBMS_ASSERT.SIMPLE_SQL_NAME(p_name), FALSE);
  END quote_name;

  FUNCTION count_rows(p_table IN VARCHAR2, p_where IN VARCHAR2 DEFAULT NULL) RETURN NUMBER IS
    l_count NUMBER;
    l_sql   VARCHAR2(32767);
  BEGIN
    l_sql := 'SELECT COUNT(*) FROM ' || quote_name(p_table);
    IF p_where IS NOT NULL THEN
      l_sql := l_sql || ' WHERE ' || p_where;
    END IF;
    EXECUTE IMMEDIATE l_sql INTO l_count;
    RETURN l_count;
  END count_rows;

  PROCEDURE set_status(p_table IN VARCHAR2, p_id IN NUMBER, p_status IN VARCHAR2, p_rows OUT NUMBER) IS
    l_old_status VARCHAR2(10);
  BEGIN
    EXECUTE IMMEDIATE
      'UPDATE ' || quote_name(p_table) || ' SET status = :new_status WHERE id = :id RETURNING status INTO :old'
      USING IN p_status, IN p_id
      RETURNING INTO l_old_status;
    p_rows := SQL%ROWCOUNT;
  END set_status;

  PROCEDURE run_block(p_proc IN VARCHAR2, p_arg IN OUT NUMBER) IS
  BEGIN
    EXECUTE IMMEDIATE 'BEGIN ' || DBMS_ASSERT.QUALIFIED_SQL_NAME(p_proc) || '(:x); END;'
      USING IN OUT p_arg;
  END run_block;

  FUNCTION fetch_ids(p_table IN VARCHAR2, p_min_amount IN NUMBER) RETURN SYS.ODCINUMBERLIST IS
    l_ids SYS.ODCINUMBERLIST;
  BEGIN
    EXECUTE IMMEDIATE
      q'[SELECT id FROM ]' || quote_name(p_table) || q'[ WHERE amount >= :a AND note NOT LIKE '%test%' ORDER BY id]'
      BULK COLLECT INTO l_ids
      USING p_min_amount;
    RETURN l_ids;
  END fetch_ids;

  PROCEDURE export_csv(p_query IN VARCHAR2, p_dir IN VARCHAR2, p_file IN VARCHAR2) IS
    l_cursor   INTEGER := DBMS_SQL.OPEN_CURSOR;
    l_columns  DBMS_SQL.DESC_TAB2;
    l_count    INTEGER;
    l_value    VARCHAR2(4000);
    l_status   INTEGER;
    l_file     UTL_FILE.FILE_TYPE;
    l_line     VARCHAR2(32767);
  BEGIN
    DBMS_SQL.PARSE(l_cursor, p_query, DBMS_SQL.NATIVE);
    DBMS_SQL.DESCRIBE_COLUMNS2(l_cursor, l_count, l_columns);

    FOR i IN 1 .. l_count LOOP
      DBMS_SQL.DEFINE_COLUMN(l_cursor, i, l_value, 4000);
      l_line := l_line || CASE WHEN i > 1 THEN ';' END || '"' || l_columns(i).col_name || '"';
    END LOOP;

    l_file := UTL_FILE.FOPEN(p_dir, p_file, 'w', 32767);
    UTL_FILE.PUT_LINE(l_file, l_line);
    l_status := DBMS_SQL.EXECUTE(l_cursor);

    WHILE DBMS_SQL.FETCH_ROWS(l_cursor) > 0 LOOP
      l_line := NULL;
      FOR i IN 1 .. l_count LOOP
        DBMS_SQL.COLUMN_VALUE(l_cursor, i, l_value);
        l_line := l_line
                  || CASE WHEN i > 1 THEN ';' END
                  || CASE
                       WHEN l_columns(i).col_type IN (DBMS_SQL.VARCHAR2_TYPE, DBMS_SQL.CHAR_TYPE)
                         THEN '"' || REPLACE(l_value, '"', '""') || '"'
                       ELSE l_value
                     END;
      END LOOP;
      UTL_FILE.PUT_LINE(l_file, l_line);
    END LOOP;

    UTL_FILE.FCLOSE(l_file);
    DBMS_SQL.CLOSE_CURSOR(l_cursor);
  EXCEPTION
    WHEN UTL_FILE.INVALID_PATH OR UTL_FILE.WRITE_ERROR THEN
      IF UTL_FILE.IS_OPEN(l_file) THEN
        UTL_FILE.FCLOSE(l_file);
      END IF;
      IF DBMS_SQL.IS_OPEN(l_cursor) THEN
        DBMS_SQL.CLOSE_CURSOR(l_cursor);
      END IF;
      RAISE_APPLICATION_ERROR(-20400, 'Cannot write ' || p_dir || '/' || p_file || ': ' || SQLERRM);
    WHEN OTHERS THEN
      IF DBMS_SQL.IS_OPEN(l_cursor) THEN
        DBMS_SQL.CLOSE_CURSOR(l_cursor);
      END IF;
      RAISE;
  END export_csv;

  PROCEDURE search(
    p_status  IN VARCHAR2 DEFAULT NULL,
    p_from    IN DATE DEFAULT NULL,
    p_to      IN DATE DEFAULT NULL,
    p_result  OUT SYS_REFCURSOR
  ) IS
    l_sql VARCHAR2(4000) := 'SELECT c.card_id, c.card_no, c.status, c.issued_at FROM cards c WHERE 1 = 1';
  BEGIN
    -- Every bind is always present so the USING list stays fixed.
    l_sql := l_sql || CASE WHEN p_status IS NOT NULL THEN ' AND c.status = :status' ELSE ' AND (1 = 1 OR :status IS NULL)' END;
    l_sql := l_sql || CASE WHEN p_from IS NOT NULL THEN ' AND c.issued_at >= :d_from' ELSE ' AND (1 = 1 OR :d_from IS NULL)' END;
    l_sql := l_sql || CASE WHEN p_to IS NOT NULL THEN ' AND c.issued_at < :d_to + 1' ELSE ' AND (1 = 1 OR :d_to IS NULL)' END;
    l_sql := l_sql || ' ORDER BY c.issued_at DESC';

    OPEN p_result FOR l_sql USING p_status, p_from, p_to;
  END search;

  PROCEDURE rebuild_indexes(p_table IN VARCHAR2, p_online IN BOOLEAN DEFAULT TRUE) IS
    e_resource_busy EXCEPTION;
    PRAGMA EXCEPTION_INIT(e_resource_busy, -54);
  BEGIN
    FOR ix IN (SELECT index_name FROM user_indexes WHERE table_name = UPPER(p_table) AND status = 'UNUSABLE') LOOP
      BEGIN
        EXECUTE IMMEDIATE 'ALTER INDEX ' || quote_name(ix.index_name) || ' REBUILD'
                          || CASE WHEN p_online THEN ' ONLINE' END
                          || ' PARALLEL 4 NOLOGGING';
        EXECUTE IMMEDIATE 'ALTER INDEX ' || quote_name(ix.index_name) || ' NOPARALLEL';
      EXCEPTION
        WHEN e_resource_busy THEN
          DBMS_LOCK.SLEEP(2);
          CONTINUE;
      END;
    END LOOP;

    DBMS_STATS.GATHER_TABLE_STATS(
      ownname          => USER,
      tabname          => UPPER(p_table),
      estimate_percent => DBMS_STATS.AUTO_SAMPLE_SIZE,
      cascade          => TRUE,
      degree           => 4);
  END rebuild_indexes;

END dyn_report;
/
