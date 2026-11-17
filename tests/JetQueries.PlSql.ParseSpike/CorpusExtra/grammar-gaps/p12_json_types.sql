DECLARE
  l_doc JSON_OBJECT_T := JSON_OBJECT_T.parse('{"id": 1}');
  l_tags JSON_ARRAY_T;
BEGIN
  l_tags := l_doc.get_Array('tags');
  l_doc.put('count', l_tags.get_size);
END;
/
