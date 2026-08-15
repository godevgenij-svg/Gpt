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
write('client/ExternalSearchManager.cpp', cpp)

print('Stage 4 post-rewrite static plumbing fix applied')
