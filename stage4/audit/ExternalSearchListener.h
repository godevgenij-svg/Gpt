#ifndef EXTERNAL_SEARCH_LISTENER_H_
#define EXTERNAL_SEARCH_LISTENER_H_

#include "ExternalSearchResult.h"

class ExternalSearchListener
{
public:
	virtual ~ExternalSearchListener() {}
	template<int I> struct X { enum { TYPE = I }; };

	typedef X<0> Result;
	typedef X<1> Status;
	typedef X<2> Error;

	virtual void on(Result, const ExternalSearch::Result&) noexcept {}
	virtual void on(Status, uint64_t, const string&) noexcept {}
	virtual void on(Error, uint64_t, const string&) noexcept {}
};

#endif // EXTERNAL_SEARCH_LISTENER_H_
