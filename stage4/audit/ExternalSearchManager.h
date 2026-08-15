#ifndef EXTERNAL_SEARCH_MANAGER_H_
#define EXTERNAL_SEARCH_MANAGER_H_

#include "Singleton.h"
#include "BaseUtil.h"
#include "Speaker.h"
#include "ExternalSearchListener.h"
#include "HttpClientListener.h"
#include "HttpClient.h"
#include "TimerManager.h"
#include "Locks.h"
#include <unordered_set>
#include <unordered_map>
#include <vector>

class ExternalSearchManager : public Singleton<ExternalSearchManager>, public Speaker<ExternalSearchListener>,
	private HttpClientListener, private TimerManagerListener
{
public:
	struct SoulseekConfig
	{
		SoulseekConfig() : enabled(false), baseUrl("http://127.0.0.1:5030"), searchTimeout(15), fileLimit(1000), responseLimit(100) {}
		bool enabled;
		string baseUrl;
		string apiKey;
		int searchTimeout;
		int fileLimit;
		int responseLimit;
	};

	struct TorznabSource
	{
		TorznabSource() : enabled(false) {}
		bool enabled;
		string name;
		string url;
		string apiKey;
	};

	struct QbittorrentConfig
	{
		QbittorrentConfig() : enabled(false), baseUrl("http://127.0.0.1:8080") {}
		bool enabled;
		string baseUrl;
		string apiKey;
		string username;
		string password;
		string savePath;
		string category;
	};

	void reloadConfig() noexcept;
	bool saveConfig(const SoulseekConfig& newSoulseek, const QbittorrentConfig& newQbittorrent,
		const std::vector<TorznabSource>& newTorznab) noexcept;
	void getConfig(SoulseekConfig& outSoulseek, QbittorrentConfig& outQbittorrent,
		std::vector<TorznabSource>& outTorznab) const noexcept;
	string getConfigPath() const { return configPath(); }

	bool hasEnabledBackends() const noexcept;
	unsigned search(const string& query, uint64_t ownerId) noexcept;
	void cancelSearch(uint64_t ownerId) noexcept;
	bool enqueueDownload(const ExternalSearch::Result& result, const string& destination = Util::emptyString) noexcept;
	void shutdown() noexcept;

private:
	friend class Singleton<ExternalSearchManager>;
	ExternalSearchManager();
	~ExternalSearchManager();

	enum RequestKind
	{
		REQ_SLSK_SEARCH,
		REQ_SLSK_RESPONSES,
		REQ_SLSK_DOWNLOAD,
		REQ_TORZNAB,
		REQ_QBT_LOGIN,
		REQ_QBT_ADD
	};

	struct PendingRequest
	{
		PendingRequest() : kind(REQ_SLSK_SEARCH), ownerId(0), retryCount(0) {}
		RequestKind kind;
		uint64_t ownerId;
		string searchId;
		TorznabSource torznabSource;
		string downloadUri;
		string destination;
		int retryCount;
	};

	struct ActiveSoulseekSearch
	{
		ActiveSoulseekSearch() : ownerId(0), nextPoll(0), expires(0), pollReqId(0) {}
		uint64_t ownerId;
		string id;
		uint64_t nextPoll;
		uint64_t expires;
		uint64_t pollReqId;
		std::unordered_set<string> seen;
	};

	mutable CriticalSection cs;
	SoulseekConfig soulseek;
	QbittorrentConfig qbittorrent;
	std::vector<TorznabSource> torznab;
	string qbSessionCookie;
	std::unordered_map<uint64_t, PendingRequest> pending;
	std::vector<ActiveSoulseekSearch> activeSoulseek;
	bool stopped;

	string configPath() const;
	void startSoulseekSearch(const string& query, uint64_t ownerId) noexcept;
	void startTorznabSearch(const string& query, uint64_t ownerId, int sourceIndex) noexcept;
	void pollSoulseek(uint64_t ownerId, const string& searchId) noexcept;
	bool enqueueSoulseek(const ExternalSearch::Result& result, const string& destination) noexcept;
	bool enqueueQbittorrent(const ExternalSearch::Result& result, const string& destination) noexcept;
	void startQbittorrentLogin(const QbittorrentConfig& qc, const string& downloadUri,
		const string& destination, uint64_t ownerId) noexcept;
	void startQbittorrentAdd(const QbittorrentConfig& qc, const string& downloadUri,
		const string& destination, uint64_t ownerId, const string& cookie, int retryCount) noexcept;
	void cleanupRequest(uint64_t requestId) noexcept;

	static string trimTrailingSlash(string s);
	static string buildTorznabUrl(const TorznabSource& source, const string& query);
	static string makeMultipartBody(const string& boundary, const string& downloadUri,
		const string& savePath, const string& category);
	static string extractSessionCookie(const Http::Response& response) noexcept;
	static void addQbittorrentHeaders(HttpClient::Request& req, const QbittorrentConfig& qc, const string& cookie);
	static bool parseSearchId(const string& json, string& id) noexcept;
	static bool parseSoulseekResponses(const string& json, uint64_t ownerId, const string& searchId,
		std::vector<ExternalSearch::Result>& results, std::unordered_set<string>& seen) noexcept;
	static bool parseTorznab(const string& xmlText, uint64_t ownerId, const TorznabSource& source,
		std::vector<ExternalSearch::Result>& results, string& error) noexcept;

	void on(HttpClientListener::Completed, uint64_t id, const Http::Response& resp, const HttpClientListener::Result& data) noexcept override;
	void on(HttpClientListener::Failed, uint64_t id, const string& error) noexcept override;
	void on(HttpClientListener::Redirected, uint64_t, const string&) noexcept override {}
	void on(TimerManagerListener::Second, uint64_t tick) noexcept override;
};

#endif // EXTERNAL_SEARCH_MANAGER_H_
