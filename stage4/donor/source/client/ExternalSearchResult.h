#ifndef EXTERNAL_SEARCH_RESULT_H_
#define EXTERNAL_SEARCH_RESULT_H_

#include "typedefs.h"
#include <cstdint>

namespace ExternalSearch
{
	enum Network
	{
		NETWORK_UNKNOWN = 0,
		NETWORK_SOULSEEK,
		NETWORK_BITTORRENT,
		NETWORK_ED2K,
		NETWORK_IPFS,
		NETWORK_GNUTELLA
	};

	struct Result
	{
		Result() : ownerId(0), network(NETWORK_UNKNOWN), size(0), freeSlot(false), queueLength(0), uploadSpeed(0), seeders(0), leechers(0), sourceCount(0), completeSourceCount(0), locked(false) {}

		uint64_t ownerId;
		Network network;
		string networkName;
		string backendName;
		string name;
		string path;
		int64_t size;
		string source;
		string hashType;
		string hash;
		string downloadUri;
		string searchId;
		bool freeSlot;
		int64_t queueLength;
		int uploadSpeed;
		int seeders;
		int leechers;
		int sourceCount;
		int completeSourceCount;
		bool locked;
	};
}

#endif // EXTERNAL_SEARCH_RESULT_H_
