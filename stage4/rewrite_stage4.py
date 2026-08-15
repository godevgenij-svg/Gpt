from pathlib import Path
import re

ROOT = Path('blacklink')

def read(rel):
    return (ROOT / rel).read_text(encoding='utf-8-sig')

def write(rel, text):
    (ROOT / rel).write_text(text, encoding='utf-8', newline='\n')

def replace_once(text, old, new, label):
    n = text.count(old)
    if n != 1:
        raise RuntimeError(f'{label}: expected exactly one match, got {n}')
    return text.replace(old, new, 1)

def regex_once(text, pattern, repl, label, flags=re.S):
    out, n = re.subn(pattern, repl, text, count=1, flags=flags)
    if n != 1:
        raise RuntimeError(f'{label}: expected exactly one regex match, got {n}')
    return out

# ---------------------------------------------------------------------------
# Provider-neutral result model: keep eD2k child ECID separate from file hash.
# ---------------------------------------------------------------------------
rel = 'client/ExternalSearchResult.h'
t = read(rel)
t = replace_once(t,
    '\t\tstring downloadUri;\n\t\tstring searchId;\n',
    '\t\tstring downloadUri;\n\t\tstring searchId;\n\t\tstring backendId; // provider-private stable item id (aMule child ECID, etc.)\n',
    'ExternalSearchResult backendId')
write(rel, t)

# ---------------------------------------------------------------------------
# Manager declarations: unified aMule auth retry for search/poll/download/stop.
# ---------------------------------------------------------------------------
rel = 'client/ExternalSearchManager.h'
t = read(rel)
t = replace_once(t,
'''\t\tAmuleConfig() : enabled(false), baseUrl("http://127.0.0.1:4713"), searchType("global"), searchTimeout(60) {}\n\t\tbool enabled;\n\t\tstring baseUrl;\n\t\tstring password;\n\t\tstring searchType;\n\t\tint searchTimeout;\n''',
'''\t\tAmuleConfig() : enabled(false), baseUrl("http://127.0.0.1:4713"), searchType("global"), searchTimeout(60), resultLimit(1000) {}\n\t\tbool enabled;\n\t\tstring baseUrl;\n\t\tstring password;\n\t\tstring searchType;\n\t\tint searchTimeout;\n\t\tint resultLimit;\n''',
    'AmuleConfig result limit')
t = replace_once(t,
'''\t\tREQ_QBT_LOGIN,\n\t\tREQ_QBT_ADD,\n\t\tREQ_AMULE_LOGIN_SEARCH,\n\t\tREQ_AMULE_LOGIN_DOWNLOAD,\n\t\tREQ_AMULE_SEARCH,\n''',
'''\t\tREQ_QBT_LOGIN,\n\t\tREQ_QBT_ADD,\n\t\tREQ_AMULE_LOGIN,\n\t\tREQ_AMULE_SEARCH,\n''',
    'unified aMule login enum')
t = replace_once(t,
'''\t\tPendingRequest() : kind(REQ_SLSK_SEARCH), ownerId(0), retryCount(0) {}\n\t\tRequestKind kind;\n\t\tuint64_t ownerId;\n\t\tstring searchId;\n\t\tstring query;\n\t\tstring searchKind;\n\t\tTorznabSource torznabSource;\n\t\tstring downloadUri;\n\t\tstring destination;\n\t\tint retryCount;\n''',
'''\t\tPendingRequest() : kind(REQ_SLSK_SEARCH), resumeKind(REQ_SLSK_SEARCH), ownerId(0), retryCount(0), closeSearch(true) {}\n\t\tRequestKind kind;\n\t\tRequestKind resumeKind;\n\t\tuint64_t ownerId;\n\t\tstring searchId;\n\t\tstring query;\n\t\tstring searchKind;\n\t\tTorznabSource torznabSource;\n\t\tstring downloadUri;\n\t\tstring destination;\n\t\tstring backendId;\n\t\tint retryCount;\n\t\tbool closeSearch;\n''',
    'PendingRequest auth continuation fields')
t = replace_once(t,
'''\tvoid startAmuleSearch(const string& query, uint64_t ownerId, int retryCount = 0) noexcept;\n\tvoid startAmuleLoginForSearch(const string& query, uint64_t ownerId) noexcept;\n\tvoid startAmuleLoginForDownload(const string& hash, uint64_t ownerId) noexcept;\n\tvoid pollAmule(uint64_t ownerId, const string& searchId) noexcept;\n\tvoid startAmuleDownload(const string& hash, uint64_t ownerId, int retryCount = 0) noexcept;\n\tvoid startAmuleStop(uint64_t ownerId, const string& searchId, bool close = true) noexcept;\n''',
'''\tvoid startAmuleSearch(const string& query, uint64_t ownerId, int retryCount = 0) noexcept;\n\tvoid startAmuleLogin(const PendingRequest& action) noexcept;\n\tvoid resumeAmuleAction(const PendingRequest& loginRequest) noexcept;\n\tvoid pollAmule(uint64_t ownerId, const string& searchId, int retryCount = 0) noexcept;\n\tvoid startAmuleDownload(const string& hash, const string& backendId, uint64_t ownerId, int retryCount = 0) noexcept;\n\tvoid startAmuleStop(uint64_t ownerId, const string& searchId, bool close = true, int retryCount = 0) noexcept;\n''',
    'aMule function declarations')
write(rel, t)

# ---------------------------------------------------------------------------
# Manager implementation.
# ---------------------------------------------------------------------------
rel = 'client/ExternalSearchManager.cpp'
t = read(rel)

# Replace the old parent-only parser with parent + children/ECID aware parser.
parser = r'''\tclass AmuleResultsParser : public JsonParser\n\t\{.*?\n\t\};\n\n\tclass SlskdResponsesParser'''
new_parser = '''\tclass AmuleResultsParser : public JsonParser
\t{
\tpublic:
\t\tAmuleResultsParser(uint64_t ownerId, const string& searchId, const string& searchKind, int resultLimit,
\t\t\tstd::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen) :
\t\t\townerId(ownerId), searchId(searchId), searchKind(searchKind), resultLimit(std::max(1, resultLimit)), results(results), seen(seen),
\t\t\tinResults(false), inResult(false), inSources(false), inChildren(false), inChild(false), inProgress(false)
\t\t{
\t\t}

\t\tstring state;

\tprotected:
\t\tbool onValue(string&, int type) noexcept override
\t\t{
\t\t\tconst int level = getNestingLevel();
\t\t\tif (type == TYPE_OBJECT && inResults && !inChildren && level == 2)
\t\t\t{
\t\t\t\titem = Item();
\t\t\t\tinResult = true;
\t\t\t}
\t\t\telse if (type == TYPE_OBJECT && inResult && inChildren && level == 4)
\t\t\t{
\t\t\t\tchild = Child();
\t\t\t\tinChild = true;
\t\t\t}
\t\t\treturn true;
\t\t}

\t\tbool onNamedValue(const string& key, string& value, int type) noexcept override
\t\t{
\t\t\tconst int level = getNestingLevel();
\t\t\tconst string k = Text::toLower(key);
\t\t\tif (level == 1)
\t\t\t{
\t\t\t\tif (k == "results" && type == TYPE_ARRAY) inResults = true;
\t\t\t\telse if (k == "progress" && type == TYPE_OBJECT) inProgress = true;
\t\t\t\treturn true;
\t\t\t}
\t\t\tif (inResult && !inChild && level == 3)
\t\t\t{
\t\t\t\tif (k == "name" && type == TYPE_STRING) item.name = value;
\t\t\t\telse if (k == "hash" && type == TYPE_STRING) item.hash = Text::toLower(value);
\t\t\t\telse if (k == "size" && type == TYPE_INT) item.size = Util::toInt64(value);
\t\t\t\telse if (k == "sources" && type == TYPE_OBJECT) inSources = true;
\t\t\t\telse if (k == "children" && type == TYPE_ARRAY) inChildren = true;
\t\t\t\treturn true;
\t\t\t}
\t\t\tif (inResult && inSources && !inChild && level == 4 && type == TYPE_INT)
\t\t\t{
\t\t\t\tif (k == "total") item.sources = Util::toInt(value);
\t\t\t\telse if (k == "complete") item.completeSources = Util::toInt(value);
\t\t\t\treturn true;
\t\t\t}
\t\t\tif (inChild && level == 5)
\t\t\t{
\t\t\t\tif (k == "name" && type == TYPE_STRING) child.name = value;
\t\t\t\telse if (k == "ecid" && type == TYPE_INT) child.ecid = value;
\t\t\t\treturn true;
\t\t\t}
\t\t\tif (inProgress && level == 2 && k == "state" && type == TYPE_STRING)
\t\t\t\tstate = Text::toLower(value);
\t\t\treturn true;
\t\t}

\t\tbool onEndStructure(int type) noexcept override
\t\t{
\t\t\tconst int level = getNestingLevel();
\t\t\tif (type == TYPE_OBJECT && inChild && level == 5)
\t\t\t{
\t\t\t\tif (!child.name.empty() && !child.ecid.empty()) item.children.push_back(child);
\t\t\t\tinChild = false;
\t\t\t}
\t\t\telse if (type == TYPE_ARRAY && inChildren && level == 4)
\t\t\t{
\t\t\t\tinChildren = false;
\t\t\t}
\t\t\telse if (type == TYPE_OBJECT && inResult && level == 4 && inSources)
\t\t\t{
\t\t\t\tinSources = false;
\t\t\t}
\t\t\telse if (type == TYPE_OBJECT && inResult && level == 3)
\t\t\t{
\t\t\t\temitItem();
\t\t\t\tinResult = false;
\t\t\t}
\t\t\telse if (type == TYPE_ARRAY && inResults && level == 2)
\t\t\t{
\t\t\t\tinResults = false;
\t\t\t}
\t\t\telse if (type == TYPE_OBJECT && inProgress && level == 2)
\t\t\t{
\t\t\t\tinProgress = false;
\t\t\t}
\t\t\treturn true;
\t\t}

\tprivate:
\t\tstruct Child
\t\t{
\t\t\tstring name;
\t\t\tstring ecid;
\t\t};
\t\tstruct Item
\t\t{
\t\t\tItem() : size(0), sources(0), completeSources(0) {}
\t\t\tstring name;
\t\t\tstring hash;
\t\t\tint64_t size;
\t\t\tint sources;
\t\t\tint completeSources;
\t\t\tstd::vector<Child> children;
\t\t};

\t\tvoid emit(const string& name, const string& backendId) noexcept
\t\t{
\t\t\tif (name.empty() || item.hash.empty() || results.size() >= static_cast<size_t>(resultLimit)) return;
\t\t\tconst string key = item.hash + "\\n" + Util::toString(item.size) + "\\n" + (backendId.empty() ? string("parent") : backendId);
\t\t\tif (!seen.insert(key).second) return;
\t\t\tExternalSearch::Result r;
\t\t\tr.ownerId = ownerId;
\t\t\tr.network = ExternalSearch::NETWORK_ED2K;
\t\t\tr.networkName = Text::toLower(searchKind) == "kad" ? "Kad" : "eD2k";
\t\t\tr.backendName = "aMule";
\t\t\tr.name = name;
\t\t\tr.path = name;
\t\t\tr.size = item.size;
\t\t\tr.source = "aMule";
\t\t\tr.hashType = "ED2K";
\t\t\tr.hash = item.hash;
\t\t\tr.searchId = searchId;
\t\t\tr.backendId = backendId;
\t\t\tr.sourceCount = item.sources;
\t\t\tr.completeSourceCount = item.completeSources;
\t\t\tresults.push_back(r);
\t\t}

\t\tvoid emitItem() noexcept
\t\t{
\t\t\tif (item.hash.empty()) return;
\t\t\temit(item.name, Util::emptyString);
\t\t\tfor (const auto& c : item.children)
\t\t\t{
\t\t\t\tif (results.size() >= static_cast<size_t>(resultLimit)) break;
\t\t\t\temit(c.name, c.ecid);
\t\t\t}
\t\t}

\t\tuint64_t ownerId;
\t\tstring searchId;
\t\tstring searchKind;
\t\tint resultLimit;
\t\tstd::vector<ExternalSearch::Result>& results;
\t\tstd::unordered_set<string>& seen;
\t\tItem item;
\t\tChild child;
\t\tbool inResults;
\t\tbool inResult;
\t\tbool inSources;
\t\tbool inChildren;
\t\tbool inChild;
\t\tbool inProgress;
\t};

\tclass SlskdResponsesParser'''
t = regex_once(t, parser, new_parser, 'AmuleResultsParser replacement')

# Config version + hidden safety result cap.
t = replace_once(t,
    'newAmule.searchTimeout = std::max(10, xml.getIntChildAttrib("SearchTimeout", newAmule.searchTimeout));\n',
    'newAmule.searchTimeout = std::max(10, xml.getIntChildAttrib("SearchTimeout", newAmule.searchTimeout));\n\t\t\t\tnewAmule.resultLimit = std::max(1, std::min(5000, xml.getIntChildAttrib("ResultLimit", newAmule.resultLimit)));\n',
    'Amule load result limit')
t = replace_once(t,
    'xml.addChildAttrib("Version", 3);\n',
    'xml.addChildAttrib("Version", 4);\n',
    'ExternalSearch config version')
t = replace_once(t,
    'xml.addChildAttrib("SearchTimeout", std::max(10, newAmule.searchTimeout));\n',
    'xml.addChildAttrib("SearchTimeout", std::max(10, newAmule.searchTimeout));\n\t\txml.addChildAttrib("ResultLimit", std::max(1, std::min(5000, newAmule.resultLimit)));\n',
    'Amule save result limit')

# Parser wrapper passes configured limit.
t = replace_once(t,
'''bool ExternalSearchManager::parseAmuleResults(const string& json, uint64_t ownerId, const string& searchId, const string& searchKind,\n\tstd::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen, string& state) noexcept\n{\n\tAmuleResultsParser p(ownerId, searchId, searchKind, results, seen);\n''',
'''bool ExternalSearchManager::parseAmuleResults(const string& json, uint64_t ownerId, const string& searchId, const string& searchKind,\n\tstd::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen, string& state) noexcept\n{\n\tint limit = 1000;\n\t{\n\t\tLOCK(cs);\n\t\tlimit = amule.resultLimit;\n\t}\n\tAmuleResultsParser p(ownerId, searchId, searchKind, limit, results, seen);\n''',
    'Amule parser result limit')

# Replace two special-purpose login functions with one continuation-based login.
login_pattern = r'''void ExternalSearchManager::startAmuleLoginForSearch\(.*?\n\}\n\nvoid ExternalSearchManager::startAmuleLoginForDownload\(.*?\n\}\n\nvoid ExternalSearchManager::startAmuleSearch'''
login_repl = '''void ExternalSearchManager::startAmuleLogin(const PendingRequest& action) noexcept
{
\tAmuleConfig ac;
\t{
\t\tLOCK(cs);
\t\tif (stopped) return;
\t\tac = amule;
\t}
\tif (!ac.enabled || ac.baseUrl.empty() || ac.password.empty() || !isSafeAmuleUrl(ac.baseUrl))
\t{
\t\tfire(ExternalSearchListener::Error(), action.ownerId, "aMule: amuleapi is not configured safely");
\t\treturn;
\t}

\tJsonFormatter jf;
\tjf.setDecorate(false);
\tjf.open('{');
\tjf.appendKey("password"); jf.appendStringValue(ac.password);
\tjf.close('}');

\tHttpClient::Request req;
\treq.type = Http::METHOD_POST;
\treq.url = ac.baseUrl + "/api/v0/auth/login";
\treq.requestBody = jf.getResult();
\treq.requestBodyType = "application/json";
\treq.closeConn = true;
\treq.noCache = true;
\treq.maxRedirects = 0;
\treq.maxRespBodySize = 1024 * 1024;
\treq.headers.push_back(std::make_pair(string("Accept"), string("application/jwt")));
\tconst uint64_t requestId = httpClient.addRequest(req);
\tif (!requestId)
\t{
\t\tfire(ExternalSearchListener::Error(), action.ownerId, "aMule: cannot create login request");
\t\treturn;
\t}
\tPendingRequest p = action;
\tp.kind = REQ_AMULE_LOGIN;
\tp.resumeKind = action.kind;
\t// A successful login consumes the only authentication retry budget.
\tp.retryCount = 1;
\t{
\t\tLOCK(cs);
\t\tpending[requestId] = p;
\t}
\thttpClient.startRequest(requestId);
}

void ExternalSearchManager::resumeAmuleAction(const PendingRequest& p) noexcept
{
\tswitch (p.resumeKind)
\t{
\t\tcase REQ_AMULE_SEARCH: startAmuleSearch(p.query, p.ownerId, p.retryCount); break;
\t\tcase REQ_AMULE_RESULTS: pollAmule(p.ownerId, p.searchId, p.retryCount); break;
\t\tcase REQ_AMULE_DOWNLOAD: startAmuleDownload(p.downloadUri, p.backendId, p.ownerId, p.retryCount); break;
\t\tcase REQ_AMULE_STOP: startAmuleStop(p.ownerId, p.searchId, p.closeSearch, p.retryCount); break;
\t\tdefault: fire(ExternalSearchListener::Error(), p.ownerId, "aMule: invalid action after login"); break;
\t}
}

void ExternalSearchManager::startAmuleSearch'''
t = regex_once(t, login_pattern, login_repl, 'unified aMule login functions')

# Search: login continuation rather than old helper.
t = replace_once(t,
'''\tif (token.empty())\n\t{\n\t\tstartAmuleLoginForSearch(query, ownerId);\n\t\treturn;\n\t}\n''',
'''\tif (token.empty())\n\t{\n\t\tPendingRequest action;\n\t\taction.kind = REQ_AMULE_SEARCH;\n\t\taction.ownerId = ownerId;\n\t\taction.query = query;\n\t\taction.retryCount = retryCount;\n\t\tstartAmuleLogin(action);\n\t\treturn;\n\t}\n''',
    'aMule search login continuation')

# Poll: authenticate on empty token and carry retry count.
t = replace_once(t,
'void ExternalSearchManager::pollAmule(uint64_t ownerId, const string& searchId) noexcept\n',
'void ExternalSearchManager::pollAmule(uint64_t ownerId, const string& searchId, int retryCount) noexcept\n',
    'poll signature')
t = replace_once(t,
'''\tif (token.empty() || !isSafeAmuleUrl(ac.baseUrl)) return;\n\n\tHttpClient::Request req;\n''',
'''\tif (!isSafeAmuleUrl(ac.baseUrl)) return;\n\tif (token.empty())\n\t{\n\t\tPendingRequest action;\n\t\taction.kind = REQ_AMULE_RESULTS;\n\t\taction.ownerId = ownerId;\n\t\taction.searchId = searchId;\n\t\taction.retryCount = retryCount;\n\t\tstartAmuleLogin(action);\n\t\treturn;\n\t}\n\n\tHttpClient::Request req;\n''',
    'poll auth continuation')
t = replace_once(t,
'''\tp.kind = REQ_AMULE_RESULTS;\n\tp.ownerId = ownerId;\n\tp.searchId = searchId;\n''',
'''\tp.kind = REQ_AMULE_RESULTS;\n\tp.ownerId = ownerId;\n\tp.searchId = searchId;\n\tp.retryCount = retryCount;\n''',
    'poll retry count')

# Download: exact child ECID support + generic login.
t = replace_once(t,
'void ExternalSearchManager::startAmuleDownload(const string& hash, uint64_t ownerId, int retryCount) noexcept\n',
'void ExternalSearchManager::startAmuleDownload(const string& hash, const string& backendId, uint64_t ownerId, int retryCount) noexcept\n',
    'download signature')
t = replace_once(t,
'''\tif (token.empty())\n\t{\n\t\tstartAmuleLoginForDownload(hash, ownerId);\n\t\treturn;\n\t}\n\n\tHttpClient::Request req;\n''',
'''\tif (token.empty())\n\t{\n\t\tPendingRequest action;\n\t\taction.kind = REQ_AMULE_DOWNLOAD;\n\t\taction.ownerId = ownerId;\n\t\taction.downloadUri = hash;\n\t\taction.backendId = backendId;\n\t\taction.retryCount = retryCount;\n\t\tstartAmuleLogin(action);\n\t\treturn;\n\t}\n\n\tHttpClient::Request req;\n''',
    'download auth continuation')
t = replace_once(t,
'''\treq.requestBodyType = "application/json";\n\treq.requestBody = "{}";\n''',
'''\treq.requestBodyType = "application/json";\n\tif (!backendId.empty() && Util::toInt64(backendId) > 0)\n\t{\n\t\tJsonFormatter jf;\n\t\tjf.setDecorate(false);\n\t\tjf.open('{');\n\t\tjf.appendKey("ecid"); jf.appendInt64Value(Util::toInt64(backendId));\n\t\tjf.close('}');\n\t\treq.requestBody = jf.getResult();\n\t}\n\telse req.requestBody = "{}";\n''',
    'download ECID body')
t = replace_once(t,
'''\tp.downloadUri = hash;\n\tp.retryCount = retryCount;\n''',
'''\tp.downloadUri = hash;\n\tp.backendId = backendId;\n\tp.retryCount = retryCount;\n''',
    'download backend id pending')

# Stop: it also participates in auth retry and carries close semantics.
t = replace_once(t,
'void ExternalSearchManager::startAmuleStop(uint64_t ownerId, const string& searchId, bool close) noexcept\n',
'void ExternalSearchManager::startAmuleStop(uint64_t ownerId, const string& searchId, bool close, int retryCount) noexcept\n',
    'stop signature')
t = replace_once(t,
'''\tif (searchId.empty() || token.empty() || !isSafeAmuleUrl(ac.baseUrl)) return;\n\n\tJsonFormatter jf;\n''',
'''\tif (searchId.empty() || !isSafeAmuleUrl(ac.baseUrl)) return;\n\tif (token.empty())\n\t{\n\t\tPendingRequest action;\n\t\taction.kind = REQ_AMULE_STOP;\n\t\taction.ownerId = ownerId;\n\t\taction.searchId = searchId;\n\t\taction.closeSearch = close;\n\t\taction.retryCount = retryCount;\n\t\tstartAmuleLogin(action);\n\t\treturn;\n\t}\n\n\tJsonFormatter jf;\n''',
    'stop auth continuation')
t = replace_once(t,
'''\tp.kind = REQ_AMULE_STOP;\n\tp.ownerId = ownerId;\n\tp.searchId = searchId;\n''',
'''\tp.kind = REQ_AMULE_STOP;\n\tp.ownerId = ownerId;\n\tp.searchId = searchId;\n\tp.closeSearch = close;\n\tp.retryCount = retryCount;\n''',
    'stop pending state')
t = replace_once(t,
'startAmuleDownload(result.hash, result.ownerId);\n',
'startAmuleDownload(result.hash, result.backendId, result.ownerId);\n',
    'enqueue exact aMule child')

# Completed: clear poll ownership before handling status; auth retry applies to all
# mutating/poll actions and cannot recurse indefinitely.
needle = '''\tconst int code = resp.getResponseCode();\n\n\t// aMule bearer tokens are cached. If an old token is rejected, clear it and\n\t// perform exactly one password login before retrying the user action.\n\tif (code == 401 && p.retryCount == 0 && (p.kind == REQ_AMULE_SEARCH || p.kind == REQ_AMULE_DOWNLOAD))\n\t{\n\t\t{\n\t\t\tLOCK(cs);\n\t\t\tamuleBearer.clear();\n\t\t}\n\t\tif (p.kind == REQ_AMULE_SEARCH) startAmuleLoginForSearch(p.query, p.ownerId);\n\t\telse startAmuleLoginForDownload(p.downloadUri, p.ownerId);\n\t\treturn;\n\t}\n'''
replacement = '''\tconst int code = resp.getResponseCode();\n\n\t// A completed poll no longer owns the in-flight slot, regardless of HTTP status.\n\tif (p.kind == REQ_AMULE_RESULTS)\n\t{\n\t\tLOCK(cs);\n\t\tfor (auto& a : activeAmule)\n\t\t\tif (a.ownerId == p.ownerId && a.id == p.searchId && a.pollReqId == id) { a.pollReqId = 0; break; }\n\t}\n\n\t// amuleapi bearer tokens are cached. On the first 401 stop using the stale\n\t// token, authenticate exactly once and resume only that action. This includes\n\t// polling and cleanup requests so a stale JWT cannot create an auth loop.\n\tconst bool amuleAction = p.kind == REQ_AMULE_SEARCH || p.kind == REQ_AMULE_RESULTS ||\n\t\tp.kind == REQ_AMULE_DOWNLOAD || p.kind == REQ_AMULE_STOP;\n\tif (code == 401 && p.retryCount == 0 && amuleAction)\n\t{\n\t\t{\n\t\t\tLOCK(cs);\n\t\t\tamuleBearer.clear();\n\t\t}\n\t\tp.retryCount = 1;\n\t\tstartAmuleLogin(p);\n\t\treturn;\n\t}\n'''
t = replace_once(t, needle, replacement, 'all-action aMule 401 retry')

# Prevent poll login failures from being retried every timer tick.
needle = '''\tif (code < 200 || code >= 300)\n\t{\n\t\tstring suffix;\n'''
replacement = '''\tif (code < 200 || code >= 300)\n\t{\n\t\tif (p.kind == REQ_AMULE_LOGIN && p.resumeKind == REQ_AMULE_RESULTS)\n\t\t{\n\t\t\tLOCK(cs);\n\t\t\tfor (auto& a : activeAmule)\n\t\t\t\tif (a.ownerId == p.ownerId && a.id == p.searchId) { a.pollReqId = 0; a.finished = true; break; }\n\t\t}\n\t\tstring suffix;\n'''
t = replace_once(t, needle, replacement, 'poll login failure guard')

# Unified login completion.
login_completed_pattern = r'''\tif \(p\.kind == REQ_AMULE_LOGIN_SEARCH \|\| p\.kind == REQ_AMULE_LOGIN_DOWNLOAD\)\n\t\{.*?\n\t\}\n\n\tif \(p\.kind == REQ_AMULE_SEARCH\)'''
login_completed_repl = '''\tif (p.kind == REQ_AMULE_LOGIN)
\t{
\t\tstring token, role;
\t\tif (!parseAmuleLogin(data.responseBody, token, role) || role != "admin")
\t\t{
\t\t\tif (p.resumeKind == REQ_AMULE_RESULTS)
\t\t\t{
\t\t\t\tLOCK(cs);
\t\t\t\tfor (auto& a : activeAmule)
\t\t\t\t\tif (a.ownerId == p.ownerId && a.id == p.searchId) { a.pollReqId = 0; a.finished = true; break; }
\t\t\t}
\t\t\tfire(ExternalSearchListener::Error(), p.ownerId, "aMule: amuleapi login failed or admin role was not granted");
\t\t\treturn;
\t\t}
\t\t{
\t\t\tLOCK(cs);
\t\t\tamuleBearer = token;
\t\t}
\t\tresumeAmuleAction(p);
\t\treturn;
\t}

\tif (p.kind == REQ_AMULE_SEARCH)'''
t = regex_once(t, login_completed_pattern, login_completed_repl, 'unified login completion')

# Poll success no longer needs to clear pollReqId here (already done at entry).
t = replace_once(t,
'''\t\t\t\t\ta.pollReqId = 0;\n\t\t\t\t\tok = parseAmuleResults(data.responseBody, p.ownerId, p.searchId, a.kind, results, a.seen, state);\n''',
'''\t\t\t\t\tok = parseAmuleResults(data.responseBody, p.ownerId, p.searchId, a.kind, results, a.seen, state);\n''',
    'remove duplicate pollReqId clear')

# Cancellation: distinguish login continuation type instead of removed login enums.
t = replace_once(t,
'''\t\t\tconst bool downloadAction = kind == REQ_SLSK_DOWNLOAD || kind == REQ_QBT_LOGIN || kind == REQ_QBT_ADD ||\n\t\t\t\tkind == REQ_AMULE_LOGIN_DOWNLOAD || kind == REQ_AMULE_DOWNLOAD || kind == REQ_AMULE_STOP;\n''',
'''\t\t\tconst bool amuleLoginCleanup = kind == REQ_AMULE_LOGIN &&\n\t\t\t\t(i->second.resumeKind == REQ_AMULE_DOWNLOAD || i->second.resumeKind == REQ_AMULE_STOP);\n\t\t\tconst bool downloadAction = kind == REQ_SLSK_DOWNLOAD || kind == REQ_QBT_LOGIN || kind == REQ_QBT_ADD ||\n\t\t\t\tamuleLoginCleanup || kind == REQ_AMULE_DOWNLOAD || kind == REQ_AMULE_STOP;\n''',
    'cancel unified login classification')

# Best-effort daemon cleanup during shutdown if a valid JWT is already cached.
shutdown_pattern = r'''void ExternalSearchManager::shutdown\(\) noexcept\n\{.*?\n\}\s*$'''
shutdown_repl = '''void ExternalSearchManager::shutdown() noexcept
{
\tstd::vector<uint64_t> requests;
\tstd::vector<std::pair<uint64_t, string> > searchesToClose;
\tbool canClose = false;
\t{
\t\tLOCK(cs);
\t\tif (stopped) return;
\t\tcanClose = !amuleBearer.empty() && amule.enabled && isSafeAmuleUrl(amule.baseUrl);
\t\tif (canClose)
\t\t\tfor (const auto& a : activeAmule) searchesToClose.push_back(std::make_pair(a.ownerId, a.id));
\t}
\t// Queue close:true while the manager is still operational. Stop requests are
\t// intentionally not cancelled below; responses are not required during exit.
\tif (canClose)
\t\tfor (const auto& a : searchesToClose) startAmuleStop(a.first, a.second, true, 1);

\t{
\t\tLOCK(cs);
\t\tif (stopped) return;
\t\tstopped = true;
\t\tfor (const auto& p : pending)
\t\t\tif (p.second.kind != REQ_AMULE_STOP) requests.push_back(p.first);
\t\tpending.clear();
\t\tfor (const auto& s : activeSoulseek) if (s.pollReqId) requests.push_back(s.pollReqId);
\t\tactiveSoulseek.clear();
\t\tfor (const auto& a : activeAmule) if (a.pollReqId) requests.push_back(a.pollReqId);
\t\tactiveAmule.clear();
\t\tamuleBearer.clear();
\t}
\tfor (uint64_t id : requests) httpClient.cancelRequest(id);
\tif (TimerManager::isValidInstance()) TimerManager::getInstance()->removeListener(this);
\thttpClient.removeListener(this);
\tremoveListeners();
}
'''
t = regex_once(t, shutdown_pattern, shutdown_repl, 'shutdown close cleanup')
write(rel, t)

# ---------------------------------------------------------------------------
# Shipped config: Stage 4 schema v4 + bounded aMule results.
# ---------------------------------------------------------------------------
rel = 'compiled/Settings/ExternalSearch.xml'
t = read(rel)
t = replace_once(t, '<ExternalSearch Version="3">', '<ExternalSearch Version="4">', 'settings config version')
t = replace_once(t,
    '<Amule Enabled="0" BaseUrl="http://127.0.0.1:4713" Password="" SearchType="global" SearchTimeout="60" />',
    '<Amule Enabled="0" BaseUrl="http://127.0.0.1:4713" Password="" SearchType="global" SearchTimeout="60" ResultLimit="1000" />',
    'settings aMule result limit')
write(rel, t)

# ---------------------------------------------------------------------------
# Sanity checks: no removed auth helpers remain and native DC files are untouched
# by this script. The authoring workflow performs git diff --check afterwards.
# ---------------------------------------------------------------------------
manager_h = read('client/ExternalSearchManager.h')
manager_cpp = read('client/ExternalSearchManager.cpp')
for forbidden in ('REQ_AMULE_LOGIN_SEARCH', 'REQ_AMULE_LOGIN_DOWNLOAD', 'startAmuleLoginForSearch', 'startAmuleLoginForDownload'):
    if forbidden in manager_h or forbidden in manager_cpp:
        raise RuntimeError(f'stale aMule auth symbol remains: {forbidden}')
for required in ('backendId', 'REQ_AMULE_LOGIN', 'startAmuleLogin', 'resumeAmuleAction', '/api/v0/search/stop', '"ecid"'):
    if required not in manager_h + manager_cpp + read('client/ExternalSearchResult.h'):
        raise RuntimeError(f'missing required Stage 4 symbol: {required}')

print('Stage 4 verified rewrite applied successfully')
