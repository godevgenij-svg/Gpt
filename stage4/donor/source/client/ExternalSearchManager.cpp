#include "stdinc.h"
#include "ExternalSearchManager.h"
#include "HttpClient.h"
#include "HttpHeaders.h"
#include "JsonFormatter.h"
#include "JsonParser.h"
#include "SimpleXML.h"
#include "File.h"
#include "AppPaths.h"
#include "UriUtil.h"
#include "PathUtil.h"
#include "StrUtil.h"
#include "LogManager.h"
#include "TimeUtil.h"
#include <algorithm>

namespace
{
	static const int EXTERNAL_SEARCH_MAX_HTTP_BODY = 16 * 1024 * 1024;
	static const unsigned SLSKD_POLL_INTERVAL = 2000;
	static const unsigned AMULE_POLL_INTERVAL = 2000;

	static string remoteFileName(const string& path)
	{
		const size_t p1 = path.find_last_of('/');
		const size_t p2 = path.find_last_of('\\');
		size_t pos = string::npos;
		if (p1 == string::npos) pos = p2;
		else if (p2 == string::npos) pos = p1;
		else pos = std::max(p1, p2);
		return pos == string::npos ? path : path.substr(pos + 1);
	}

	class SearchIdParser : public JsonParser
	{
	public:
		string id;
	protected:
		bool onNamedValue(const string& key, string& value, int type) noexcept override
		{
			if (getNestingLevel() == 1 && Text::toLower(key) == "id" && type == TYPE_STRING)
				id = value;
			return true;
		}
	};

	class AmuleLoginParser : public JsonParser
	{
	public:
		string token;
		string role;
	protected:
		bool onNamedValue(const string& key, string& value, int type) noexcept override
		{
			if (getNestingLevel() != 1 || type != TYPE_STRING) return true;
			const string k = Text::toLower(key);
			if (k == "token") token = value;
			else if (k == "role") role = Text::toLower(value);
			return true;
		}
	};

	class AmuleSearchStartParser : public JsonParser
	{
	public:
		string id;
	protected:
		bool onNamedValue(const string& key, string& value, int type) noexcept override
		{
			if (getNestingLevel() == 1 && Text::toLower(key) == "search_id" && type == TYPE_INT)
				id = value;
			return true;
		}
	};

	class AmuleResultsParser : public JsonParser
	{
	public:
		AmuleResultsParser(uint64_t ownerId, const string& searchId, const string& searchKind,
			std::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen) :
			ownerId(ownerId), searchId(searchId), searchKind(searchKind), results(results), seen(seen),
			inResults(false), inResult(false), inSources(false), inProgress(false)
		{
		}

		string state;

	protected:
		bool onValue(string&, int type) noexcept override
		{
			if (type == TYPE_OBJECT && inResults && getNestingLevel() == 2)
			{
				item = Item();
				inResult = true;
			}
			return true;
		}

		bool onNamedValue(const string& key, string& value, int type) noexcept override
		{
			const int level = getNestingLevel();
			const string k = Text::toLower(key);
			if (level == 1)
			{
				if (k == "results" && type == TYPE_ARRAY) inResults = true;
				else if (k == "progress" && type == TYPE_OBJECT) inProgress = true;
				return true;
			}
			if (inResult && level == 3)
			{
				if (k == "name" && type == TYPE_STRING) item.name = value;
				else if (k == "hash" && type == TYPE_STRING) item.hash = Text::toLower(value);
				else if (k == "size" && type == TYPE_INT) item.size = Util::toInt64(value);
				else if (k == "sources" && type == TYPE_OBJECT) inSources = true;
				return true;
			}
			if (inResult && inSources && level == 4 && type == TYPE_INT)
			{
				if (k == "total") item.sources = Util::toInt(value);
				else if (k == "complete") item.completeSources = Util::toInt(value);
				return true;
			}
			if (inProgress && level == 2)
			{
				if (k == "state" && type == TYPE_STRING) state = Text::toLower(value);
			}
			return true;
		}

		bool onEndStructure(int type) noexcept override
		{
			const int level = getNestingLevel();
			if (type == TYPE_OBJECT && inResult && level == 4 && inSources)
			{
				inSources = false;
			}
			else if (type == TYPE_OBJECT && inResult && level == 3)
			{
				emit();
				inResult = false;
			}
			else if (type == TYPE_ARRAY && inResults && level == 2)
			{
				inResults = false;
			}
			else if (type == TYPE_OBJECT && inProgress && level == 2)
			{
				inProgress = false;
			}
			return true;
		}

	private:
		struct Item
		{
			Item() : size(0), sources(0), completeSources(0) {}
			string name;
			string hash;
			int64_t size;
			int sources;
			int completeSources;
		};

		void emit() noexcept
		{
			if (item.name.empty() || item.hash.empty()) return;
			const string key = item.hash + "\n" + Util::toString(item.size);
			if (!seen.insert(key).second) return;
			ExternalSearch::Result r;
			r.ownerId = ownerId;
			r.network = ExternalSearch::NETWORK_ED2K;
			r.networkName = Text::toLower(searchKind) == "kad" ? "Kad" : "eD2k";
			r.backendName = "aMule";
			r.name = item.name;
			r.path = item.name;
			r.size = item.size;
			r.source = "aMule";
			r.hashType = "ED2K";
			r.hash = item.hash;
			r.searchId = searchId;
			r.sourceCount = item.sources;
			r.completeSourceCount = item.completeSources;
			results.push_back(r);
		}

		uint64_t ownerId;
		string searchId;
		string searchKind;
		std::vector<ExternalSearch::Result>& results;
		std::unordered_set<string>& seen;
		Item item;
		bool inResults;
		bool inResult;
		bool inSources;
		bool inProgress;
	};

	class SlskdResponsesParser : public JsonParser
	{
	public:
		SlskdResponsesParser(uint64_t ownerId, const string& searchId,
			std::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen) :
			ownerId(ownerId), searchId(searchId), results(results), seen(seen), inResponse(false), inFiles(false), inLockedFiles(false), inFile(false)
		{
		}

	protected:
		bool onValue(string&, int type) noexcept override
		{
			const int level = getNestingLevel();
			if (type == TYPE_OBJECT && level == 1)
			{
				response = ResponseData();
				inResponse = true;
			}
			else if (type == TYPE_OBJECT && level == 3 && (inFiles || inLockedFiles))
			{
				file = FileData();
				file.locked = inLockedFiles;
				inFile = true;
			}
			return true;
		}

		bool onNamedValue(const string& key, string& value, int type) noexcept override
		{
			const int level = getNestingLevel();
			const string keyLower = Text::toLower(key);
			if (inResponse && level == 2)
			{
				if (type == TYPE_ARRAY && keyLower == "files")
					inFiles = true;
				else if (type == TYPE_ARRAY && keyLower == "lockedfiles")
					inLockedFiles = true;
				else if (keyLower == "username" && type == TYPE_STRING)
					response.username = value;
				else if (keyLower == "hasfreeuploadslot" && type == TYPE_BOOL)
					response.freeSlot = value == "true";
				else if (keyLower == "queuelength" && type == TYPE_INT)
					response.queueLength = Util::toInt64(value);
				else if (keyLower == "uploadspeed" && type == TYPE_INT)
					response.uploadSpeed = Util::toInt(value);
			}
			else if (inFile && level == 4)
			{
				if (keyLower == "filename" && type == TYPE_STRING)
					file.filename = value;
				else if (keyLower == "size" && type == TYPE_INT)
					file.size = Util::toInt64(value);
				else if (keyLower == "islocked" && type == TYPE_BOOL)
					file.locked = value == "true";
			}
			return true;
		}

		bool onEndStructure(int type) noexcept override
		{
			const int level = getNestingLevel();
			if (type == TYPE_OBJECT && inFile && level == 4)
			{
				response.files.push_back(file);
				inFile = false;
			}
			else if (type == TYPE_ARRAY && level == 3)
			{
				if (inFiles) inFiles = false;
				else if (inLockedFiles) inLockedFiles = false;
			}
			else if (type == TYPE_OBJECT && inResponse && level == 2)
			{
				emitResponse();
				inResponse = false;
			}
			return true;
		}

	private:
		struct FileData
		{
			FileData() : size(0), locked(false) {}
			string filename;
			int64_t size;
			bool locked;
		};
		struct ResponseData
		{
			ResponseData() : freeSlot(false), queueLength(0), uploadSpeed(0) {}
			string username;
			bool freeSlot;
			int64_t queueLength;
			int uploadSpeed;
			std::vector<FileData> files;
		};

		void emitResponse() noexcept
		{
			if (response.username.empty()) return;
			for (const auto& f : response.files)
			{
				if (f.filename.empty()) continue;
				const string key = response.username + "\n" + f.filename + "\n" + Util::toString(f.size);
				if (!seen.insert(key).second) continue;

				ExternalSearch::Result r;
				r.ownerId = ownerId;
				r.network = ExternalSearch::NETWORK_SOULSEEK;
				r.networkName = "Soulseek";
				r.backendName = "slskd";
				r.path = f.filename;
				r.name = remoteFileName(f.filename);
				if (r.name.empty()) r.name = f.filename;
				r.size = f.size;
				r.source = response.username;
				r.searchId = searchId;
				r.freeSlot = response.freeSlot;
				r.queueLength = response.queueLength;
				r.uploadSpeed = response.uploadSpeed;
				r.locked = f.locked;
				results.push_back(r);
			}
		}

		uint64_t ownerId;
		string searchId;
		std::vector<ExternalSearch::Result>& results;
		std::unordered_set<string>& seen;
		ResponseData response;
		FileData file;
		bool inResponse;
		bool inFiles;
		bool inLockedFiles;
		bool inFile;
	};


	static void replaceAll(string& s, const string& from, const string& to)
	{
		if (from.empty()) return;
		size_t pos = 0;
		while ((pos = s.find(from, pos)) != string::npos)
		{
			s.replace(pos, from.length(), to);
			pos += to.length();
		}
	}

	static void appendQueryParam(string& url, const string& name, const string& value)
	{
		url += url.find('?') == string::npos ? '?' : '&';
		url += name;
		url += '=';
		url += Util::encodeUriQuery(value);
	}
}

ExternalSearchManager::ExternalSearchManager() : stopped(false)
{
	reloadConfig();
	httpClient.addListener(this);
	TimerManager::getInstance()->addListener(this);
}

ExternalSearchManager::~ExternalSearchManager()
{
	shutdown();
}

string ExternalSearchManager::configPath() const
{
	return Util::getConfigPath() + "ExternalSearch.xml";
}

string ExternalSearchManager::trimTrailingSlash(string s)
{
	while (!s.empty() && s.back() == '/') s.pop_back();
	return s;
}

void ExternalSearchManager::reloadConfig() noexcept
{
	SoulseekConfig newSoulseek;
	AmuleConfig newAmule;
	QbittorrentConfig newQbittorrent;
	std::vector<TorznabSource> newTorznab;
	const string newPath = configPath();
	const string legacyPath = Util::getConfigPath() + "GreyBridge.xml";
	const bool useLegacy = File::getSize(newPath) < 0 && File::getSize(legacyPath) >= 0;
	const string loadPath = useLegacy ? legacyPath : newPath;
	try
	{
		File f(loadPath, File::READ, File::OPEN);
		const string data = f.read();
		SimpleXML xml;
		xml.fromXML(data);
		if (xml.findChild(useLegacy ? "GreyBridge" : "ExternalSearch"))
		{
			xml.stepIn();
			if (xml.findChild("Soulseek"))
			{
				newSoulseek.enabled = xml.getBoolChildAttrib("Enabled");
				newSoulseek.baseUrl = trimTrailingSlash(xml.getChildAttrib("BaseUrl", newSoulseek.baseUrl));
				newSoulseek.apiKey = xml.getChildAttrib("ApiKey");
				newSoulseek.searchTimeout = std::max(5, xml.getIntChildAttrib("SearchTimeout", newSoulseek.searchTimeout));
				newSoulseek.fileLimit = std::max(1, xml.getIntChildAttrib("FileLimit", newSoulseek.fileLimit));
				newSoulseek.responseLimit = std::max(1, xml.getIntChildAttrib("ResponseLimit", newSoulseek.responseLimit));
			}
			xml.resetCurrentChild();
			if (xml.findChild("Amule"))
			{
				newAmule.enabled = xml.getBoolChildAttrib("Enabled");
				newAmule.baseUrl = trimTrailingSlash(xml.getChildAttrib("BaseUrl", newAmule.baseUrl));
				newAmule.password = xml.getChildAttrib("Password");
				newAmule.searchType = Text::toLower(xml.getChildAttrib("SearchType", newAmule.searchType));
				if (newAmule.searchType != "local" && newAmule.searchType != "global" && newAmule.searchType != "kad")
					newAmule.searchType = "global";
				newAmule.searchTimeout = std::max(10, xml.getIntChildAttrib("SearchTimeout", newAmule.searchTimeout));
			}
			xml.resetCurrentChild();
			if (xml.findChild("QBittorrent"))
			{
				newQbittorrent.enabled = xml.getBoolChildAttrib("Enabled");
				newQbittorrent.baseUrl = trimTrailingSlash(xml.getChildAttrib("BaseUrl", newQbittorrent.baseUrl));
				newQbittorrent.apiKey = xml.getChildAttrib("ApiKey");
				newQbittorrent.username = xml.getChildAttrib("Username");
				newQbittorrent.password = xml.getChildAttrib("Password");
				newQbittorrent.savePath = xml.getChildAttrib("SavePath");
				newQbittorrent.category = xml.getChildAttrib("Category");
			}
			xml.resetCurrentChild();
			if (xml.findChild("Torznab"))
			{
				xml.stepIn();
				while (xml.findChild("Source"))
				{
					TorznabSource source;
					source.enabled = xml.getBoolChildAttrib("Enabled");
					source.name = xml.getChildAttrib("Name", "Torznab");
					source.url = xml.getChildAttrib("Url");
					source.apiKey = xml.getChildAttrib("ApiKey");
					if (!source.url.empty()) newTorznab.push_back(source);
				}
				xml.stepOut();
			}
		}
	}
	catch (const Exception& e)
	{
		// A missing file is expected on the first run. Keep safe defaults, but
		// report malformed/unreadable files in the technical log.
		if (File::getSize(loadPath) >= 0)
			LogManager::message("External Search: failed to load " + loadPath + ": " + e.getError(), false);
	}

	{
		LOCK(cs);
		soulseek = newSoulseek;
		amule = newAmule;
		qbittorrent = newQbittorrent;
		torznab = newTorznab;
		qbSessionCookie.clear();
		amuleBearer.clear();
	}

	// One-time compatibility path: keep old settings working, but write the new
	// neutral file name/root so the current project no longer depends on the legacy naming.
	if (useLegacy)
	{
		LogManager::message("External Search: migrating legacy GreyBridge.xml to ExternalSearch.xml", false);
		saveConfig(newSoulseek, newAmule, newQbittorrent, newTorznab);
	}
}

bool ExternalSearchManager::saveConfig(const SoulseekConfig& newSoulseek, const AmuleConfig& newAmule, const QbittorrentConfig& newQbittorrent,
	const std::vector<TorznabSource>& newTorznab) noexcept
{
	try
	{
		SimpleXML xml;
		xml.addTag("ExternalSearch");
		xml.addChildAttrib("Version", 3);
		xml.stepIn();

		xml.addTag("Soulseek");
		xml.addChildAttrib("Enabled", newSoulseek.enabled);
		xml.addChildAttrib("BaseUrl", trimTrailingSlash(newSoulseek.baseUrl));
		xml.addChildAttrib("ApiKey", newSoulseek.apiKey);
		xml.addChildAttrib("SearchTimeout", std::max(5, newSoulseek.searchTimeout));
		xml.addChildAttrib("FileLimit", std::max(1, newSoulseek.fileLimit));
		xml.addChildAttrib("ResponseLimit", std::max(1, newSoulseek.responseLimit));

		xml.addTag("Amule");
		xml.addChildAttrib("Enabled", newAmule.enabled);
		xml.addChildAttrib("BaseUrl", trimTrailingSlash(newAmule.baseUrl));
		xml.addChildAttrib("Password", newAmule.password);
		xml.addChildAttrib("SearchType", newAmule.searchType);
		xml.addChildAttrib("SearchTimeout", std::max(10, newAmule.searchTimeout));

		xml.addTag("QBittorrent");
		xml.addChildAttrib("Enabled", newQbittorrent.enabled);
		xml.addChildAttrib("BaseUrl", trimTrailingSlash(newQbittorrent.baseUrl));
		xml.addChildAttrib("ApiKey", newQbittorrent.apiKey);
		xml.addChildAttrib("Username", newQbittorrent.username);
		xml.addChildAttrib("Password", newQbittorrent.password);
		xml.addChildAttrib("SavePath", newQbittorrent.savePath);
		xml.addChildAttrib("Category", newQbittorrent.category);

		xml.addTag("Torznab");
		xml.stepIn();
		for (const auto& source : newTorznab)
		{
			if (source.url.empty()) continue;
			xml.addTag("Source");
			xml.addChildAttrib("Enabled", source.enabled);
			xml.addChildAttrib("Name", source.name.empty() ? "Torznab" : source.name);
			xml.addChildAttrib("Url", source.url);
			xml.addChildAttrib("ApiKey", source.apiKey);
		}
		xml.stepOut();
		xml.stepOut();

		const string path = configPath();
		const string tempPath = path + ".tmp";
		{
			File f(tempPath, File::WRITE, File::CREATE | File::TRUNCATE);
			f.write(SimpleXML::utf8Header);
			f.write(xml.toXML());
			f.close();
		}
		if (!File::renameFile(tempPath, path))
		{
			File::deleteFile(tempPath);
			LogManager::message("External Search: failed to replace " + path, false);
			return false;
		}
		reloadConfig();
		return true;
	}
	catch (const Exception& e)
	{
		LogManager::message("External Search: failed to save " + configPath() + ": " + e.getError(), false);
		return false;
	}
}

void ExternalSearchManager::getConfig(SoulseekConfig& outSoulseek, AmuleConfig& outAmule, QbittorrentConfig& outQbittorrent,
	std::vector<TorznabSource>& outTorznab) const noexcept
{
	LOCK(cs);
	outSoulseek = soulseek;
	outAmule = amule;
	outQbittorrent = qbittorrent;
	outTorznab = torznab;
}

bool ExternalSearchManager::hasEnabledBackends() const noexcept
{
	LOCK(cs);
	if (stopped) return false;
	if (soulseek.enabled && !soulseek.baseUrl.empty()) return true;
	if (amule.enabled && !amule.baseUrl.empty() && !amule.password.empty() && isSafeAmuleUrl(amule.baseUrl)) return true;
	for (const auto& source : torznab)
		if (source.enabled && !source.url.empty()) return true;
	return false;
}

unsigned ExternalSearchManager::search(const string& query, uint64_t ownerId) noexcept
{
	if (query.empty()) return 0;
	cancelSearch(ownerId);
	unsigned maxTime = 0;
	SoulseekConfig sc;
	AmuleConfig ac;
	std::vector<TorznabSource> tz;
	{
		LOCK(cs);
		if (stopped) return 0;
		sc = soulseek;
		ac = amule;
		tz = torznab;
	}
	if (sc.enabled && !sc.baseUrl.empty())
	{
		startSoulseekSearch(query, ownerId);
		maxTime = std::max<unsigned>(maxTime, (unsigned) (sc.searchTimeout + 5) * 1000);
	}
	if (ac.enabled && !ac.baseUrl.empty() && !ac.password.empty())
	{
		if (isSafeAmuleUrl(ac.baseUrl))
		{
			startAmuleSearch(query, ownerId);
			maxTime = std::max<unsigned>(maxTime, (unsigned) (ac.searchTimeout + 5) * 1000);
		}
		else
		{
			fire(ExternalSearchListener::Error(), ownerId, "aMule: remote amuleapi requires HTTPS; plain HTTP is allowed only on localhost");
		}
	}
	for (size_t i = 0; i < tz.size(); ++i)
	{
		if (tz[i].enabled && !tz[i].url.empty())
		{
			startTorznabSearch(query, ownerId, (int) i);
			maxTime = std::max<unsigned>(maxTime, 30000);
		}
	}
	return maxTime;
}

void ExternalSearchManager::startSoulseekSearch(const string& query, uint64_t ownerId) noexcept
{
	SoulseekConfig sc;
	{
		LOCK(cs);
		sc = soulseek;
	}
	JsonFormatter jf;
	jf.setDecorate(false);
	jf.open('{');
	jf.appendKey("searchText"); jf.appendStringValue(query);
	jf.appendKey("searchTimeout"); jf.appendIntValue(sc.searchTimeout);
	jf.appendKey("fileLimit"); jf.appendIntValue(sc.fileLimit);
	jf.appendKey("responseLimit"); jf.appendIntValue(sc.responseLimit);
	jf.close('}');

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = sc.baseUrl + "/api/v0/searches";
	req.requestBody = jf.getResult();
	req.requestBodyType = "application/json";
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = EXTERNAL_SEARCH_MAX_HTTP_BODY;
	if (!sc.apiKey.empty()) req.headers.push_back(std::make_pair(string("X-API-Key"), sc.apiKey));
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId)
	{
		fire(ExternalSearchListener::Error(), ownerId, string("External Search/Soulseek: cannot create HTTP request"));
		return;
	}
	PendingRequest p;
	p.kind = REQ_SLSK_SEARCH;
	p.ownerId = ownerId;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
	fire(ExternalSearchListener::Status(), ownerId, string("Soulseek: поиск запущен"));
}

string ExternalSearchManager::buildTorznabUrl(const TorznabSource& source, const string& query)
{
	string url = source.url;
	const bool hasQueryPlaceholder = url.find("{query}") != string::npos;
	const bool hasApiPlaceholder = url.find("{apikey}") != string::npos;
	replaceAll(url, "{query}", Util::encodeUriQuery(query));
	replaceAll(url, "{apikey}", Util::encodeUriQuery(source.apiKey));
	if (!hasQueryPlaceholder)
	{
		if (url.find("t=") == string::npos) appendQueryParam(url, "t", "search");
		appendQueryParam(url, "q", query);
	}
	if (!source.apiKey.empty() && !hasApiPlaceholder && url.find("apikey=") == string::npos)
		appendQueryParam(url, "apikey", source.apiKey);
	return url;
}

string ExternalSearchManager::makeMultipartBody(const string& boundary, const string& downloadUri,
	const string& savePath, const string& category)
{
	string body;
	auto addField = [&](const string& name, const string& value)
	{
		if (value.empty()) return;
		body += "--" + boundary + "\r\n";
		body += "Content-Disposition: form-data; name=\"" + name + "\"\r\n\r\n";
		body += value;
		body += "\r\n";
	};
	addField("urls", downloadUri);
	addField("savepath", savePath);
	addField("category", category);
	body += "--" + boundary + "--\r\n";
	return body;
}

string ExternalSearchManager::extractSessionCookie(const Http::Response& response) noexcept
{
	const string& setCookie = response.getHeaderValue(Http::HEADER_SET_COOKIE);
	if (setCookie.empty()) return Util::emptyString;

	// Current qBittorrent uses QBT_SID_<webui-port>; older versions used SID.
	// Keep the complete cookie name=value pair because the name is intentionally
	// port-specific and must be echoed back unchanged.
	size_t start = setCookie.find("QBT_SID_");
	if (start == string::npos) start = setCookie.find("SID=");
	if (start == string::npos) return Util::emptyString;
	const size_t end = setCookie.find(';', start);
	return setCookie.substr(start, end == string::npos ? string::npos : end - start);
}

void ExternalSearchManager::addQbittorrentHeaders(HttpClient::Request& req, const QbittorrentConfig& qc, const string& cookie)
{
	// qBittorrent's WebUI API validates Origin/Referer against the request host.
	// Use the exact configured origin, including the port.
	if (!qc.baseUrl.empty())
	{
		req.headers.push_back(std::make_pair(string("Origin"), qc.baseUrl));
		req.headers.push_back(std::make_pair(string("Referer"), qc.baseUrl + "/"));
	}
	if (!qc.apiKey.empty())
		req.headers.push_back(std::make_pair(string("Authorization"), string("Bearer ") + qc.apiKey));
	else if (!cookie.empty())
		req.headers.push_back(std::make_pair(string("Cookie"), cookie));
}

void ExternalSearchManager::startQbittorrentLogin(const QbittorrentConfig& qc, const string& downloadUri,
	const string& destination, uint64_t ownerId) noexcept
{
	if (qc.baseUrl.empty() || qc.username.empty())
	{
		fire(ExternalSearchListener::Error(), ownerId, "qBittorrent: не заданы URL/логин WebUI");
		return;
	}

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = qc.baseUrl + "/api/v2/auth/login";
	req.requestBody = "username=" + Util::encodeUriQuery(qc.username) + "&password=" + Util::encodeUriQuery(qc.password);
	req.requestBodyType = "application/x-www-form-urlencoded";
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = 1024 * 1024;
	addQbittorrentHeaders(req, qc, Util::emptyString);

	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId)
	{
		fire(ExternalSearchListener::Error(), ownerId, "qBittorrent: не удалось создать запрос авторизации");
		return;
	}
	PendingRequest p;
	p.kind = REQ_QBT_LOGIN;
	p.ownerId = ownerId;
	p.downloadUri = downloadUri;
	p.destination = destination;
	p.retryCount = 1; // after a login we do not recursively retry forever
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
}

void ExternalSearchManager::startQbittorrentAdd(const QbittorrentConfig& qc, const string& downloadUri,
	const string& destination, uint64_t ownerId, const string& cookie, int retryCount) noexcept
{
	if (qc.baseUrl.empty() || downloadUri.empty()) return;
	const string boundary = "----ExternalSearch" + Util::toString(GET_TICK()) + Util::toString(ownerId);
	const string savePath = destination.empty() ? qc.savePath : destination;

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = qc.baseUrl + "/api/v2/torrents/add";
	req.requestBody = makeMultipartBody(boundary, downloadUri, savePath, qc.category);
	req.requestBodyType = "multipart/form-data; boundary=" + boundary;
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = 2 * 1024 * 1024;
	addQbittorrentHeaders(req, qc, cookie);

	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId)
	{
		fire(ExternalSearchListener::Error(), ownerId, "qBittorrent: не удалось создать запрос добавления торрента");
		return;
	}
	PendingRequest p;
	p.kind = REQ_QBT_ADD;
	p.ownerId = ownerId;
	p.downloadUri = downloadUri;
	p.destination = destination;
	p.retryCount = retryCount;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
}

bool ExternalSearchManager::isSafeAmuleUrl(const string& url) noexcept
{
	Util::ParsedUrl p;
	Util::decodeUrl(url, p, "http");
	const string protocol = Text::toLower(p.protocol);
	const string host = Text::toLower(p.host);
	if (host.empty()) return false;
	if (protocol == "https") return true;
	if (protocol != "http") return false;
	return host == "localhost" || host == "::1" || host == "127.0.0.1";
}

void ExternalSearchManager::addAmuleAuthHeader(HttpClient::Request& req, const string& token)
{
	if (!token.empty()) req.headers.push_back(std::make_pair(string("Authorization"), string("Bearer ") + token));
}

bool ExternalSearchManager::parseAmuleLogin(const string& json, string& token, string& role) noexcept
{
	AmuleLoginParser p;
	p.setFlags(JsonParser::FLAG_VALIDATE_UTF8 | JsonParser::FLAG_STRICT_NUMBER_CHECKS);
	if (p.process(json.data(), json.size()) != JsonParser::NO_ERROR || p.finish() != JsonParser::NO_ERROR) return false;
	token = p.token;
	role = p.role;
	return !token.empty();
}

bool ExternalSearchManager::parseAmuleSearchStart(const string& json, string& searchId) noexcept
{
	AmuleSearchStartParser p;
	p.setFlags(JsonParser::FLAG_VALIDATE_UTF8 | JsonParser::FLAG_STRICT_NUMBER_CHECKS);
	if (p.process(json.data(), json.size()) != JsonParser::NO_ERROR || p.finish() != JsonParser::NO_ERROR) return false;
	searchId = p.id;
	return !searchId.empty() && Util::toInt64(searchId) > 0;
}

bool ExternalSearchManager::parseAmuleResults(const string& json, uint64_t ownerId, const string& searchId, const string& searchKind,
	std::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen, string& state) noexcept
{
	AmuleResultsParser p(ownerId, searchId, searchKind, results, seen);
	p.setFlags(JsonParser::FLAG_VALIDATE_UTF8 | JsonParser::FLAG_STRICT_NUMBER_CHECKS);
	if (p.process(json.data(), json.size()) != JsonParser::NO_ERROR || p.finish() != JsonParser::NO_ERROR) return false;
	state = p.state;
	return true;
}

void ExternalSearchManager::startAmuleLoginForSearch(const string& query, uint64_t ownerId) noexcept
{
	AmuleConfig ac;
	{
		LOCK(cs);
		if (stopped) return;
		ac = amule;
	}
	if (!ac.enabled || ac.baseUrl.empty() || ac.password.empty() || !isSafeAmuleUrl(ac.baseUrl))
	{
		fire(ExternalSearchListener::Error(), ownerId, "aMule: amuleapi is not configured safely");
		return;
	}

	JsonFormatter jf;
	jf.setDecorate(false);
	jf.open('{');
	jf.appendKey("password"); jf.appendStringValue(ac.password);
	jf.close('}');

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = ac.baseUrl + "/api/v0/auth/login";
	req.requestBody = jf.getResult();
	req.requestBodyType = "application/json";
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = 1024 * 1024;
	req.headers.push_back(std::make_pair(string("Accept"), string("application/jwt")));
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId)
	{
		fire(ExternalSearchListener::Error(), ownerId, "aMule: cannot create login request");
		return;
	}
	PendingRequest p;
	p.kind = REQ_AMULE_LOGIN_SEARCH;
	p.ownerId = ownerId;
	p.query = query;
	p.searchKind = ac.searchType;
	p.retryCount = 1;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
}

void ExternalSearchManager::startAmuleLoginForDownload(const string& hash, uint64_t ownerId) noexcept
{
	AmuleConfig ac;
	{
		LOCK(cs);
		if (stopped) return;
		ac = amule;
	}
	if (!ac.enabled || ac.baseUrl.empty() || ac.password.empty() || !isSafeAmuleUrl(ac.baseUrl))
	{
		fire(ExternalSearchListener::Error(), ownerId, "aMule: amuleapi is not configured safely");
		return;
	}

	JsonFormatter jf;
	jf.setDecorate(false);
	jf.open('{');
	jf.appendKey("password"); jf.appendStringValue(ac.password);
	jf.close('}');

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = ac.baseUrl + "/api/v0/auth/login";
	req.requestBody = jf.getResult();
	req.requestBodyType = "application/json";
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = 1024 * 1024;
	req.headers.push_back(std::make_pair(string("Accept"), string("application/jwt")));
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId)
	{
		fire(ExternalSearchListener::Error(), ownerId, "aMule: cannot create login request");
		return;
	}
	PendingRequest p;
	p.kind = REQ_AMULE_LOGIN_DOWNLOAD;
	p.ownerId = ownerId;
	p.downloadUri = hash;
	p.retryCount = 1;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
}

void ExternalSearchManager::startAmuleSearch(const string& query, uint64_t ownerId, int retryCount) noexcept
{
	AmuleConfig ac;
	string token;
	{
		LOCK(cs);
		if (stopped) return;
		ac = amule;
		token = amuleBearer;
	}
	if (!ac.enabled || ac.baseUrl.empty() || ac.password.empty()) return;
	if (!isSafeAmuleUrl(ac.baseUrl))
	{
		fire(ExternalSearchListener::Error(), ownerId, "aMule: remote amuleapi requires HTTPS; plain HTTP is allowed only on localhost");
		return;
	}
	if (token.empty())
	{
		startAmuleLoginForSearch(query, ownerId);
		return;
	}

	JsonFormatter jf;
	jf.setDecorate(false);
	jf.open('{');
	jf.appendKey("query"); jf.appendStringValue(query);
	jf.appendKey("type"); jf.appendStringValue(ac.searchType);
	jf.close('}');

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = ac.baseUrl + "/api/v0/search";
	req.requestBody = jf.getResult();
	req.requestBodyType = "application/json";
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = 2 * 1024 * 1024;
	addAmuleAuthHeader(req, token);
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId)
	{
		fire(ExternalSearchListener::Error(), ownerId, "aMule: cannot create search request");
		return;
	}
	PendingRequest p;
	p.kind = REQ_AMULE_SEARCH;
	p.ownerId = ownerId;
	p.query = query;
	p.searchKind = ac.searchType;
	p.retryCount = retryCount;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
	fire(ExternalSearchListener::Status(), ownerId, string("aMule/") + ac.searchType + ": поиск запущен");
}

void ExternalSearchManager::pollAmule(uint64_t ownerId, const string& searchId) noexcept
{
	AmuleConfig ac;
	string token;
	{
		LOCK(cs);
		if (stopped) return;
		ac = amule;
		token = amuleBearer;
	}
	if (token.empty() || !isSafeAmuleUrl(ac.baseUrl)) return;

	HttpClient::Request req;
	req.type = Http::METHOD_GET;
	req.url = ac.baseUrl + "/api/v0/search/results?search_id=" + Util::encodeUriQuery(searchId);
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = EXTERNAL_SEARCH_MAX_HTTP_BODY;
	addAmuleAuthHeader(req, token);
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId) return;
	PendingRequest p;
	p.kind = REQ_AMULE_RESULTS;
	p.ownerId = ownerId;
	p.searchId = searchId;
	{
		LOCK(cs);
		pending[requestId] = p;
		for (auto& s : activeAmule)
			if (s.ownerId == ownerId && s.id == searchId) { s.pollReqId = requestId; break; }
	}
	httpClient.startRequest(requestId);
}

void ExternalSearchManager::startAmuleDownload(const string& hash, uint64_t ownerId, int retryCount) noexcept
{
	AmuleConfig ac;
	string token;
	{
		LOCK(cs);
		if (stopped) return;
		ac = amule;
		token = amuleBearer;
	}
	if (hash.empty() || !ac.enabled || ac.baseUrl.empty() || ac.password.empty()) return;
	if (!isSafeAmuleUrl(ac.baseUrl))
	{
		fire(ExternalSearchListener::Error(), ownerId, "aMule: remote amuleapi requires HTTPS; plain HTTP is allowed only on localhost");
		return;
	}
	if (token.empty())
	{
		startAmuleLoginForDownload(hash, ownerId);
		return;
	}

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = ac.baseUrl + "/api/v0/search/results/" + Util::encodeUriPath(hash) + "/download";
	req.requestBodyType = "application/json";
	req.requestBody = "{}";
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = 2 * 1024 * 1024;
	addAmuleAuthHeader(req, token);
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId)
	{
		fire(ExternalSearchListener::Error(), ownerId, "aMule: cannot create download request");
		return;
	}
	PendingRequest p;
	p.kind = REQ_AMULE_DOWNLOAD;
	p.ownerId = ownerId;
	p.downloadUri = hash;
	p.retryCount = retryCount;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
}

void ExternalSearchManager::startAmuleStop(uint64_t ownerId, const string& searchId, bool close) noexcept
{
	AmuleConfig ac;
	string token;
	{
		LOCK(cs);
		if (stopped) return;
		ac = amule;
		token = amuleBearer;
	}
	if (searchId.empty() || token.empty() || !isSafeAmuleUrl(ac.baseUrl)) return;

	JsonFormatter jf;
	jf.setDecorate(false);
	jf.open('{');
	jf.appendKey("search_id"); jf.appendInt64Value(Util::toInt64(searchId));
	jf.appendKey("close"); jf.appendBoolValue(close);
	jf.close('}');

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = ac.baseUrl + "/api/v0/search/stop";
	req.requestBody = jf.getResult();
	req.requestBodyType = "application/json";
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = 1024 * 1024;
	addAmuleAuthHeader(req, token);
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId) return;
	PendingRequest p;
	p.kind = REQ_AMULE_STOP;
	p.ownerId = ownerId;
	p.searchId = searchId;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
}

bool ExternalSearchManager::enqueueAmule(const ExternalSearch::Result& result) noexcept
{
	if (result.hash.empty()) return false;
	AmuleConfig ac;
	{
		LOCK(cs);
		if (stopped || !amule.enabled || amule.baseUrl.empty() || amule.password.empty()) return false;
		ac = amule;
	}
	if (!isSafeAmuleUrl(ac.baseUrl)) return false;
	startAmuleDownload(result.hash, result.ownerId);
	return true;
}

void ExternalSearchManager::startTorznabSearch(const string& query, uint64_t ownerId, int sourceIndex) noexcept
{
	TorznabSource source;
	{
		LOCK(cs);
		if (sourceIndex < 0 || sourceIndex >= (int) torznab.size()) return;
		source = torznab[sourceIndex];
	}
	HttpClient::Request req;
	req.type = Http::METHOD_GET;
	req.url = buildTorznabUrl(source, query);
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 3;
	req.maxRespBodySize = EXTERNAL_SEARCH_MAX_HTTP_BODY;
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId)
	{
		fire(ExternalSearchListener::Error(), ownerId, "External Search/Torznab: cannot create HTTP request");
		return;
	}
	PendingRequest p;
	p.kind = REQ_TORZNAB;
	p.ownerId = ownerId;
	p.torznabSource = source;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
	fire(ExternalSearchListener::Status(), ownerId, "BitTorrent/Torznab: поиск запущен через " + source.name);
}

void ExternalSearchManager::pollSoulseek(uint64_t ownerId, const string& searchId) noexcept
{
	SoulseekConfig sc;
	{
		LOCK(cs);
		sc = soulseek;
	}
	HttpClient::Request req;
	req.type = Http::METHOD_GET;
	req.url = sc.baseUrl + "/api/v0/searches/" + searchId + "/responses";
	req.closeConn = true;
	req.noCache = true;
	req.maxRedirects = 0;
	req.maxRespBodySize = EXTERNAL_SEARCH_MAX_HTTP_BODY;
	if (!sc.apiKey.empty()) req.headers.push_back(std::make_pair(string("X-API-Key"), sc.apiKey));
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId) return;
	PendingRequest p;
	p.kind = REQ_SLSK_RESPONSES;
	p.ownerId = ownerId;
	p.searchId = searchId;
	{
		LOCK(cs);
		pending[requestId] = p;
		for (auto& s : activeSoulseek)
			if (s.ownerId == ownerId && s.id == searchId) { s.pollReqId = requestId; break; }
	}
	httpClient.startRequest(requestId);
}

bool ExternalSearchManager::parseSearchId(const string& json, string& id) noexcept
{
	SearchIdParser p;
	p.setFlags(JsonParser::FLAG_VALIDATE_UTF8 | JsonParser::FLAG_STRICT_NUMBER_CHECKS);
	if (p.process(json.data(), json.size()) != JsonParser::NO_ERROR || p.finish() != JsonParser::NO_ERROR)
		return false;
	id = p.id;
	return !id.empty();
}

bool ExternalSearchManager::parseSoulseekResponses(const string& json, uint64_t ownerId, const string& searchId,
	std::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen) noexcept
{
	SlskdResponsesParser p(ownerId, searchId, results, seen);
	p.setFlags(JsonParser::FLAG_VALIDATE_UTF8 | JsonParser::FLAG_STRICT_NUMBER_CHECKS);
	return p.process(json.data(), json.size()) == JsonParser::NO_ERROR && p.finish() == JsonParser::NO_ERROR;
}

bool ExternalSearchManager::parseTorznab(const string& xmlText, uint64_t ownerId, const TorznabSource& source,
	std::vector<ExternalSearch::Result>& results, string& error) noexcept
{
	try
	{
		SimpleXML xml;
		xml.fromXML(xmlText);
		if (!xml.findChild("rss")) { error = "Torznab: root <rss> not found"; return false; }
		xml.stepIn();
		if (!xml.findChild("channel")) { error = "Torznab: <channel> not found"; return false; }
		xml.stepIn();
		while (xml.findChild("item"))
		{
			ExternalSearch::Result r;
			r.ownerId = ownerId;
			r.network = ExternalSearch::NETWORK_BITTORRENT;
			r.networkName = "BitTorrent";
			r.backendName = source.name.empty() ? "Torznab" : source.name;
			string link, enclosure, guid, infoHash, magnet;
			xml.stepIn();
			while (xml.getNextChild())
			{
				const string tag = xml.getChildTag();
				if (tag == "title") r.name = xml.getChildData();
				else if (tag == "link") link = xml.getChildData();
				else if (tag == "guid") guid = xml.getChildData();
				else if (tag == "size") r.size = Util::toInt64(xml.getChildData());
				else if (tag == "enclosure")
				{
					enclosure = xml.getChildAttrib("url");
					if (!xml.getChildAttrib("length").empty()) r.size = Util::toInt64(xml.getChildAttrib("length"));
				}
				else if (tag == "torznab:attr")
				{
					const string name = Text::toLower(xml.getChildAttrib("name"));
					const string value = xml.getChildAttrib("value");
					if (name == "seeders") r.seeders = Util::toInt(value);
					else if (name == "leechers" || name == "peers") r.leechers = Util::toInt(value);
					else if (name == "size" && !value.empty()) r.size = Util::toInt64(value);
					else if (name == "infohash") infoHash = value;
					else if (name == "magneturl") magnet = value;
				}
			}
			xml.stepOut();
			if (r.name.empty()) r.name = guid;
			r.path = r.name;
			r.source = r.backendName;
			r.downloadUri = !magnet.empty() ? magnet : (!enclosure.empty() ? enclosure : link);
			if (!infoHash.empty())
			{
				r.hashType = "BTIH";
				r.hash = infoHash;
			}
			if (!r.name.empty()) results.push_back(r);
		}
		return true;
	}
	catch (const Exception& e)
	{
		error = e.getError();
		return false;
	}
}

void ExternalSearchManager::on(HttpClientListener::Completed, uint64_t id, const Http::Response& resp, const HttpClientListener::Result& data) noexcept
{
	PendingRequest p;
	bool found = false;
	{
		LOCK(cs);
		auto i = pending.find(id);
		if (i != pending.end()) { p = i->second; pending.erase(i); found = true; }
	}
	if (!found) return;
	const int code = resp.getResponseCode();

	// aMule bearer tokens are cached. If an old token is rejected, clear it and
	// perform exactly one password login before retrying the user action.
	if (code == 401 && p.retryCount == 0 && (p.kind == REQ_AMULE_SEARCH || p.kind == REQ_AMULE_DOWNLOAD))
	{
		{
			LOCK(cs);
			amuleBearer.clear();
		}
		if (p.kind == REQ_AMULE_SEARCH) startAmuleLoginForSearch(p.query, p.ownerId);
		else startAmuleLoginForDownload(p.downloadUri, p.ownerId);
		return;
	}

	// qBittorrent session cookies may expire. Retry a single time by logging in
	// again, but never retry API-key authentication or recurse indefinitely.
	if (p.kind == REQ_QBT_ADD && (code == 401 || code == 403) && p.retryCount == 0)
	{
		QbittorrentConfig qc;
		{
			LOCK(cs);
			qc = qbittorrent;
			qbSessionCookie.clear();
		}
		if (qc.apiKey.empty() && !qc.username.empty())
		{
			startQbittorrentLogin(qc, p.downloadUri, p.destination, p.ownerId);
			return;
		}
	}

	if (code < 200 || code >= 300)
	{
		string suffix;
		if (!data.responseBody.empty())
		{
			suffix = data.responseBody.substr(0, 512);
			suffix.erase(std::remove(suffix.begin(), suffix.end(), '\r'), suffix.end());
			replaceAll(suffix, "\n", " ");
			if (!suffix.empty()) suffix = ": " + suffix;
		}
		fire(ExternalSearchListener::Error(), p.ownerId, "External Search HTTP " + Util::toString(code) + " for " + data.url + suffix);
		return;
	}

	if (p.kind == REQ_AMULE_LOGIN_SEARCH || p.kind == REQ_AMULE_LOGIN_DOWNLOAD)
	{
		string token, role;
		if (!parseAmuleLogin(data.responseBody, token, role) || role != "admin")
		{
			fire(ExternalSearchListener::Error(), p.ownerId, "aMule: amuleapi login failed or admin role was not granted");
			return;
		}
		{
			LOCK(cs);
			amuleBearer = token;
		}
		if (p.kind == REQ_AMULE_LOGIN_SEARCH) startAmuleSearch(p.query, p.ownerId, 1);
		else startAmuleDownload(p.downloadUri, p.ownerId, 1);
		return;
	}

	if (p.kind == REQ_AMULE_SEARCH)
	{
		string searchId;
		if (!parseAmuleSearchStart(data.responseBody, searchId))
		{
			fire(ExternalSearchListener::Error(), p.ownerId, "aMule: invalid search start response");
			return;
		}
		ActiveAmuleSearch a;
		a.ownerId = p.ownerId;
		a.id = searchId;
		a.kind = p.searchKind.empty() ? "global" : p.searchKind;
		a.nextPoll = GET_TICK();
		{
			LOCK(cs);
			a.expires = GET_TICK() + (uint64_t) std::max(10, amule.searchTimeout) * 1000;
			activeAmule.push_back(a);
		}
		pollAmule(p.ownerId, searchId);
		return;
	}

	if (p.kind == REQ_AMULE_RESULTS)
	{
		std::vector<ExternalSearch::Result> results;
		string state;
		bool ok = false;
		bool finished = false;
		{
			LOCK(cs);
			for (auto& a : activeAmule)
			{
				if (a.ownerId == p.ownerId && a.id == p.searchId)
				{
					a.pollReqId = 0;
					ok = parseAmuleResults(data.responseBody, p.ownerId, p.searchId, a.kind, results, a.seen, state);
					finished = state == "finished";
					a.finished = finished;
					if (!finished) a.nextPoll = GET_TICK() + AMULE_POLL_INTERVAL;
					break;
				}
			}
		}
		if (!ok)
		{
			fire(ExternalSearchListener::Error(), p.ownerId, "aMule: failed to parse eD2k/Kad search results");
			return;
		}
		for (const auto& r : results) fire(ExternalSearchListener::Result(), r);
		if (finished) fire(ExternalSearchListener::Status(), p.ownerId, "aMule: поиск завершён, результаты оставлены доступными для загрузки");
		return;
	}

	if (p.kind == REQ_AMULE_DOWNLOAD)
	{
		fire(ExternalSearchListener::Status(), p.ownerId, "eD2k/Kad: файл передан в aMule");
		return;
	}

	if (p.kind == REQ_AMULE_STOP) return;

	if (p.kind == REQ_QBT_LOGIN)
	{
		const string cookie = extractSessionCookie(resp);
		if (cookie.empty() || Text::toLower(data.responseBody).find("fail") != string::npos)
		{
			fire(ExternalSearchListener::Error(), p.ownerId, "qBittorrent: авторизация WebUI не удалась");
			return;
		}
		QbittorrentConfig qc;
		{
			LOCK(cs);
			qbSessionCookie = cookie;
			qc = qbittorrent;
		}
		startQbittorrentAdd(qc, p.downloadUri, p.destination, p.ownerId, cookie, p.retryCount);
		return;
	}

	if (p.kind == REQ_QBT_ADD)
	{
		fire(ExternalSearchListener::Status(), p.ownerId, "BitTorrent: торрент передан в qBittorrent");
		return;
	}

	if (p.kind == REQ_SLSK_SEARCH)
	{
		string searchId;
		if (!parseSearchId(data.responseBody, searchId))
		{
			fire(ExternalSearchListener::Error(), p.ownerId, "Soulseek/slskd: invalid search response");
			return;
		}
		ActiveSoulseekSearch s;
		s.ownerId = p.ownerId;
		s.id = searchId;
		s.nextPoll = GET_TICK();
		{
			LOCK(cs);
			s.expires = GET_TICK() + (uint64_t) (soulseek.searchTimeout + 5) * 1000;
			activeSoulseek.push_back(s);
		}
		pollSoulseek(p.ownerId, searchId);
		return;
	}
	if (p.kind == REQ_SLSK_RESPONSES)
	{
		std::vector<ExternalSearch::Result> results;
		bool ok = false;
		{
			LOCK(cs);
			for (auto& s : activeSoulseek)
			{
				if (s.ownerId == p.ownerId && s.id == p.searchId)
				{
					s.pollReqId = 0;
					s.nextPoll = GET_TICK() + SLSKD_POLL_INTERVAL;
					ok = parseSoulseekResponses(data.responseBody, p.ownerId, p.searchId, results, s.seen);
					break;
				}
			}
		}
		if (!ok)
		{
			fire(ExternalSearchListener::Error(), p.ownerId, "Soulseek/slskd: failed to parse search responses");
			return;
		}
		for (const auto& r : results) fire(ExternalSearchListener::Result(), r);
		return;
	}
	if (p.kind == REQ_TORZNAB)
	{
		const TorznabSource& source = p.torznabSource;
		std::vector<ExternalSearch::Result> results;
		string error;
		if (!parseTorznab(data.responseBody, p.ownerId, source, results, error))
		{
			fire(ExternalSearchListener::Error(), p.ownerId, "Torznab/" + source.name + ": " + error);
			return;
		}
		for (const auto& r : results) fire(ExternalSearchListener::Result(), r);
		fire(ExternalSearchListener::Status(), p.ownerId, "Torznab/" + source.name + ": найдено " + Util::toString(results.size()));
		return;
	}
	if (p.kind == REQ_SLSK_DOWNLOAD)
		fire(ExternalSearchListener::Status(), p.ownerId, "Soulseek: файл добавлен в очередь slskd");
}

void ExternalSearchManager::on(HttpClientListener::Failed, uint64_t id, const string& error) noexcept
{
	PendingRequest p;
	bool found = false;
	{
		LOCK(cs);
		auto i = pending.find(id);
		if (i != pending.end()) { p = i->second; pending.erase(i); found = true; }
		if (found && p.kind == REQ_SLSK_RESPONSES)
			for (auto& s : activeSoulseek) if (s.pollReqId == id) { s.pollReqId = 0; s.nextPoll = GET_TICK() + SLSKD_POLL_INTERVAL; break; }
		if (found && p.kind == REQ_AMULE_RESULTS)
			for (auto& a : activeAmule) if (a.pollReqId == id) { a.pollReqId = 0; if (!a.finished) a.nextPoll = GET_TICK() + AMULE_POLL_INTERVAL; break; }
	}
	if (found) fire(ExternalSearchListener::Error(), p.ownerId, "External Search: " + error);
}

void ExternalSearchManager::on(TimerManagerListener::Second, uint64_t tick) noexcept
{
	std::vector<std::pair<uint64_t, string> > soulseekPolls;
	std::vector<std::pair<uint64_t, string> > amulePolls;
	std::vector<uint64_t> cancelIds;
	std::vector<std::pair<uint64_t, string> > amuleTimeouts;
	{
		LOCK(cs);
		if (stopped) return;
		for (auto i = activeSoulseek.begin(); i != activeSoulseek.end(); )
		{
			if (tick >= i->expires)
			{
				if (i->pollReqId) cancelIds.push_back(i->pollReqId);
				i = activeSoulseek.erase(i);
				continue;
			}
			if (!i->pollReqId && tick >= i->nextPoll)
			{
				soulseekPolls.push_back(std::make_pair(i->ownerId, i->id));
				i->nextPoll = tick + SLSKD_POLL_INTERVAL;
			}
			++i;
		}
		for (auto& a : activeAmule)
		{
			if (a.finished) continue;
			if (tick >= a.expires)
			{
				if (a.pollReqId) { cancelIds.push_back(a.pollReqId); a.pollReqId = 0; }
				a.finished = true;
				amuleTimeouts.push_back(std::make_pair(a.ownerId, a.id));
				continue;
			}
			if (!a.pollReqId && tick >= a.nextPoll)
			{
				amulePolls.push_back(std::make_pair(a.ownerId, a.id));
				a.nextPoll = tick + AMULE_POLL_INTERVAL;
			}
		}
	}
	for (uint64_t requestId : cancelIds) httpClient.cancelRequest(requestId);
	for (const auto& p : soulseekPolls) pollSoulseek(p.first, p.second);
	for (const auto& p : amulePolls) pollAmule(p.first, p.second);
	for (const auto& a : amuleTimeouts)
	{
		startAmuleStop(a.first, a.second, false);
		fire(ExternalSearchListener::Status(), a.first, "aMule: лимит ожидания поиска истёк; поиск остановлен, уже найденные результаты оставлены доступными");
	}
}

void ExternalSearchManager::cancelSearch(uint64_t ownerId) noexcept
{
	std::vector<uint64_t> cancelIds;
	std::vector<string> amuleSearchIds;
	{
		LOCK(cs);
		for (auto i = pending.begin(); i != pending.end(); )
		{
			const RequestKind kind = i->second.kind;
			const bool downloadAction = kind == REQ_SLSK_DOWNLOAD || kind == REQ_QBT_LOGIN || kind == REQ_QBT_ADD ||
				kind == REQ_AMULE_LOGIN_DOWNLOAD || kind == REQ_AMULE_DOWNLOAD || kind == REQ_AMULE_STOP;
			if (i->second.ownerId == ownerId && !downloadAction)
			{
				cancelIds.push_back(i->first);
				i = pending.erase(i);
			}
			else ++i;
		}
		for (auto i = activeSoulseek.begin(); i != activeSoulseek.end(); )
		{
			if (i->ownerId == ownerId)
			{
				if (i->pollReqId) cancelIds.push_back(i->pollReqId);
				i = activeSoulseek.erase(i);
			}
			else ++i;
		}
		for (auto i = activeAmule.begin(); i != activeAmule.end(); )
		{
			if (i->ownerId == ownerId)
			{
				if (i->pollReqId) cancelIds.push_back(i->pollReqId);
				amuleSearchIds.push_back(i->id);
				i = activeAmule.erase(i);
			}
			else ++i;
		}
	}
	for (uint64_t reqId : cancelIds) httpClient.cancelRequest(reqId);
	for (const string& searchId : amuleSearchIds) startAmuleStop(ownerId, searchId);
}

bool ExternalSearchManager::enqueueDownload(const ExternalSearch::Result& result, const string& destination) noexcept
{
	if (result.network == ExternalSearch::NETWORK_SOULSEEK)
		return enqueueSoulseek(result, destination);
	if (result.network == ExternalSearch::NETWORK_BITTORRENT)
		return enqueueQbittorrent(result, destination);
	if (result.network == ExternalSearch::NETWORK_ED2K)
		return enqueueAmule(result);
	return false;
}

bool ExternalSearchManager::enqueueSoulseek(const ExternalSearch::Result& result, const string& destination) noexcept
{
	if (result.source.empty() || result.path.empty()) return false;
	SoulseekConfig sc;
	{
		LOCK(cs);
		if (stopped || !soulseek.enabled) return false;
		sc = soulseek;
	}
	JsonFormatter jf;
	jf.setDecorate(false);
	jf.open('{');
	if (!result.searchId.empty()) { jf.appendKey("searchId"); jf.appendStringValue(result.searchId); }
	jf.appendKey("username"); jf.appendStringValue(result.source);
	jf.appendKey("files"); jf.open('['); jf.open('{');
	jf.appendKey("filename"); jf.appendStringValue(result.path);
	jf.appendKey("size"); jf.appendInt64Value(result.size);
	jf.close('}'); jf.close(']');
	jf.appendKey("options"); jf.open('{');
	if (!destination.empty()) { jf.appendKey("destination"); jf.appendStringValue(destination); }
	jf.close('}'); jf.close('}');

	HttpClient::Request req;
	req.type = Http::METHOD_POST;
	req.url = sc.baseUrl + "/api/v0/transfers/downloads/batches";
	req.requestBody = jf.getResult();
	req.requestBodyType = "application/json";
	req.closeConn = true;
	req.noCache = true;
	req.maxRespBodySize = 2 * 1024 * 1024;
	if (!sc.apiKey.empty()) req.headers.push_back(std::make_pair(string("X-API-Key"), sc.apiKey));
	const uint64_t requestId = httpClient.addRequest(req);
	if (!requestId) return false;
	PendingRequest p;
	p.kind = REQ_SLSK_DOWNLOAD;
	p.ownerId = result.ownerId;
	p.searchId = result.searchId;
	{
		LOCK(cs);
		pending[requestId] = p;
	}
	httpClient.startRequest(requestId);
	return true;
}

bool ExternalSearchManager::enqueueQbittorrent(const ExternalSearch::Result& result, const string& destination) noexcept
{
	if (result.downloadUri.empty()) return false;
	QbittorrentConfig qc;
	string cookie;
	{
		LOCK(cs);
		if (stopped || !qbittorrent.enabled || qbittorrent.baseUrl.empty()) return false;
		qc = qbittorrent;
		cookie = qbSessionCookie;
	}

	// API key authentication (qBittorrent 5.2+) is stateless and preferred.
	if (!qc.apiKey.empty())
	{
		startQbittorrentAdd(qc, result.downloadUri, destination, result.ownerId, Util::emptyString, 1);
		return true;
	}

	// Older/current installations can use the traditional WebUI session cookie.
	if (!cookie.empty())
	{
		startQbittorrentAdd(qc, result.downloadUri, destination, result.ownerId, cookie, 0);
		return true;
	}
	if (!qc.username.empty())
	{
		startQbittorrentLogin(qc, result.downloadUri, destination, result.ownerId);
		return true;
	}

	// qBittorrent may be configured to bypass authentication for localhost.
	// In that case try the request without credentials instead of refusing it.
	startQbittorrentAdd(qc, result.downloadUri, destination, result.ownerId, Util::emptyString, 1);
	return true;
}

void ExternalSearchManager::cleanupRequest(uint64_t requestId) noexcept
{
	LOCK(cs);
	pending.erase(requestId);
}

void ExternalSearchManager::shutdown() noexcept
{
	std::vector<uint64_t> requests;
	{
		LOCK(cs);
		if (stopped) return;
		stopped = true;
		for (const auto& p : pending) requests.push_back(p.first);
		pending.clear();
		for (const auto& s : activeSoulseek) if (s.pollReqId) requests.push_back(s.pollReqId);
		activeSoulseek.clear();
		for (const auto& a : activeAmule) if (a.pollReqId) requests.push_back(a.pollReqId);
		activeAmule.clear();
		amuleBearer.clear();
	}
	for (uint64_t id : requests) httpClient.cancelRequest(id);
	if (TimerManager::isValidInstance()) TimerManager::getInstance()->removeListener(this);
	httpClient.removeListener(this);
	removeListeners();
}
