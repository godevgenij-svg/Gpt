from pathlib import Path

ROOT = Path('blacklink')

def read(rel):
    return (ROOT / rel).read_text(encoding='utf-8-sig')

def write(rel, text):
    (ROOT / rel).write_text(text, encoding='utf-8', newline='\n')

def one(text, old, new, label):
    n = text.count(old)
    if n != 1:
        raise RuntimeError(f'{label}: expected 1 match, got {n}')
    return text.replace(old, new, 1)

h = read('client/ExternalSearchManager.h')
h = one(h,
'''\tstatic bool parseAmuleResults(const string& json, uint64_t ownerId, const string& searchId, const string& searchKind,\n\t\tstd::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen, string& state) noexcept;\n''',
'''\tstatic bool parseAmuleResults(const string& json, uint64_t ownerId, const string& searchId, const string& searchKind, int resultLimit,\n\t\tstd::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen, string& state) noexcept;\n''',
'header parseAmuleResults limit')
write('client/ExternalSearchManager.h', h)

cpp = read('client/ExternalSearchManager.cpp')
cpp = one(cpp,
'''bool ExternalSearchManager::parseAmuleResults(const string& json, uint64_t ownerId, const string& searchId, const string& searchKind,\n\tstd::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen, string& state) noexcept\n{\n\tint limit = 1000;\n\t{\n\t\tLOCK(cs);\n\t\tlimit = amule.resultLimit;\n\t}\n\tAmuleResultsParser p(ownerId, searchId, searchKind, limit, results, seen);\n''',
'''bool ExternalSearchManager::parseAmuleResults(const string& json, uint64_t ownerId, const string& searchId, const string& searchKind, int resultLimit,\n\tstd::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen, string& state) noexcept\n{\n\tAmuleResultsParser p(ownerId, searchId, searchKind, resultLimit, results, seen);\n''',
'cpp parseAmuleResults static limit')
cpp = one(cpp,
'''ok = parseAmuleResults(data.responseBody, p.ownerId, p.searchId, a.kind, results, a.seen, state);''',
'''ok = parseAmuleResults(data.responseBody, p.ownerId, p.searchId, a.kind, amule.resultLimit, results, a.seen, state);''',
'poll parse call limit')

# The authoring template is a Python triple-quoted string. A C++ "\\n" inside it
# was interpreted by Python and became a physical newline inside a C++ string literal.
# Repair the generated source here and assert that this class of corruption is gone.
cpp = one(cpp,
'''\t\t\tconst string key = item.hash + "\n" + Util::toString(item.size) + "\n" + (backendId.empty() ? string("parent") : backendId);''',
'''\t\t\tconst string key = item.hash + "\\n" + Util::toString(item.size) + "\\n" + (backendId.empty() ? string("parent") : backendId);''',
'generated C++ newline literal')

# Cheap compile-contract guards. These fail during authoring instead of several
# minutes later in MSBuild if the Stage 3 API or generated declarations drift.
json_header = read('client/JsonFormatter.h')
if 'void appendInt64Value(int64_t val) noexcept;' not in json_header:
    raise RuntimeError('Stage 3 JsonFormatter contract changed: appendInt64Value(int64_t) missing')

sig = 'startAmuleDownload(const string& hash, const string& backendId, uint64_t ownerId, int retryCount'
if sig not in h or sig not in cpp:
    raise RuntimeError('aMule download declaration/definition signature mismatch')

if '"\n"' in cpp:
    raise RuntimeError('Generated ExternalSearchManager.cpp still contains a physical newline inside an empty C++ string literal')

write('client/ExternalSearchManager.cpp', cpp)

print('Stage 4 post-rewrite compile guards applied')
