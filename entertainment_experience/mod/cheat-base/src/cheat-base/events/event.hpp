// Taken from https://github.com/ZolotovPavel/EventHandling

#pragma once

#include <typeinfo>
#include <type_traits>
#include <cheat-base/relic-guard.h>
#include <cheat-base/relic-diag.h>
#include <list>
#include <memory>
#include <shared_mutex>
#include <algorithm>
#include <assert.h>
#include "handlers/abstracteventhandler.hpp"
#include "handlers/eventhandlerptr.h"
#include "handlers/handlercast.hpp"
#include "joins/eventjoinwrapper.hpp"

template<class ...TParams>
struct TypeHelper
{
    using TEventHandlerPtr = ::events::handlers::TEventHandlerPtr<TParams...>;
    using TEventHandlerIt = typename std::list<TEventHandlerPtr>::const_iterator;
};


namespace joins
{
	template<class ...TParams> class HandlerEventJoin;
}

template<class ...TParams>
class IEvent
{
    public:

        template<class TSome>
        ::events::EventJoin operator+=( TSome&& some )
        {
            ::events::EventJoin result( *this, std::forward<TSome>( some ) );
            result.join();
            return result;
        }

        template<class TSome>
        bool operator-=( TSome&& some )
        {
            return removeHandler(::events::handlers::HandlerCast<TSome>::template cast<TParams...>( some ) );
        }

    protected:

        using TMyEventHandlerPtr = typename TypeHelper<TParams...>::TEventHandlerPtr;

        IEvent() {}
        virtual ~IEvent() = default;

        virtual bool isHandlerAdded( const TMyEventHandlerPtr& eventHandler ) const = 0;
        virtual bool addHandler( TMyEventHandlerPtr eventHandler ) = 0;
        virtual bool removeHandler( TMyEventHandlerPtr eventHandler ) = 0;

    friend class ::events::joins::HandlerEventJoin<TParams...>;
};

template<class ...TParams>
struct EventCore
{
    using TMyHandlerPtr = typename TypeHelper<TParams...>::TEventHandlerPtr;

    EventCore() {}
    EventCore(const EventCore<TParams...>& other) : handlers(other.handlers) {}
    EventCore(EventCore<TParams...>&& other) : handlers(std::move(handlers)) {}
    EventCore<TParams...>& operator=(const EventCore<TParams...>& other) { handlers = other.handlers; return *this; }
    EventCore<TParams...>& operator=(EventCore<TParams...>&& other) { handlers = std::move(other.handlers); return *this; }

    std::list<TMyHandlerPtr> handlers;
    mutable std::shared_mutex coreMutex;
};


template<class ...TParams>
class HandlerRunner
{
    using TMyEventCore = EventCore<TParams...>;
    using TMyHandlerIt = typename TypeHelper<TParams...>::TEventHandlerIt;

    public:

        HandlerRunner( TMyEventCore& eventCore ) :
            currentIt(),
            wasRemoving( false ),
            m_eventCore( eventCore )
        {
        }

        void run( TParams... params )
        {
            m_eventCore.coreMutex.lock_shared();

            currentIt = m_eventCore.handlers.begin();
            wasRemoving = false;
            while( currentIt != m_eventCore.handlers.end() )
            {
                m_eventCore.coreMutex.unlock_shared();
                // Relic: guard every handler on its own — a feature whose offsets are wrong for this game
                // version is skipped instead of aborting the event for everyone (which would leave the whole
                // menu dead after one bad feature). After a few faults that handler is switched off, so a
                // broken feature costs nothing per frame. The name in the log is the C++ type of the handler,
                // which carries the feature class and the method.
                {
                    auto& handler = *currentIt;
                    if (handler->relicFaults < relic::kGuardFaultLimit)
                    {
                        RELIC_DIAG_TICK(HANDLER_RUN);
                        if (relic::Try([&]() { handler->call( params... ); }))
                        {
                            handler->relicFaults = 0;   // it works again: an early fault was transient
                        }
                        else
                        {
                            handler->relicFaults++;
                            relic::diag::note_fault(typeid(*handler).name(), relic::g_lastGuardCode);
                            relic::guard_report_fault(typeid(*handler).name(), relic::g_lastGuardCode,
                                handler->relicFaults, handler->relicFaults >= relic::kGuardFaultLimit);
                        }
                    }
                    else
                        RELIC_DIAG_TICK(HANDLER_SKIP);
                }
                m_eventCore.coreMutex.lock_shared();

                if( wasRemoving )
                    wasRemoving = false;
                else
                    ++currentIt;
            }

            m_eventCore.coreMutex.unlock_shared();
        }

        TMyHandlerIt currentIt;
        mutable bool wasRemoving;

    private:

        TMyEventCore& m_eventCore;
};

template<class ...TParams>
class TEvent : public IEvent<TParams...>
{
    using TMyEventHandlerPtr = typename TypeHelper<TParams...>::TEventHandlerPtr;
    using TMyEventHandlerIt = typename TypeHelper<TParams...>::TEventHandlerIt;
    using TMyHandlerRunner = HandlerRunner<TParams...>;

    public:

        TEvent() :
            m_core()
        {
        }

        TEvent(const TEvent& other) : m_core(other.m_core), m_handlerRunners(other.m_handlerRunners)
        {

        }

        virtual void operator()( TParams... params )
        {
            TMyHandlerRunner newHandlerRunner( m_core );

            m_core.coreMutex.lock_shared();
            auto it = m_handlerRunners.insert( m_handlerRunners.end(), &newHandlerRunner );
            m_core.coreMutex.unlock_shared();

            newHandlerRunner.run( params... );

            m_core.coreMutex.lock_shared();
            m_handlerRunners.erase( it );
            m_core.coreMutex.unlock_shared();
        }

    protected:

        virtual bool isHandlerAdded( const TMyEventHandlerPtr& eventHandler ) const override
        {
            std::shared_lock<std::shared_mutex> _coreMutexLock( m_core.coreMutex );

            return ( findEventHandler( eventHandler ) != m_core.handlers.end() );

        }
        virtual bool addHandler( TMyEventHandlerPtr eventHandler ) override
        {
            std::unique_lock<std::shared_mutex> _coreMutexLock( m_core.coreMutex );

            if( findEventHandler( eventHandler ) == m_core.handlers.end() )
            {
                m_core.handlers.push_back( std::move( eventHandler ) );
                return true;
            }
            return false;
        }
        virtual bool removeHandler( TMyEventHandlerPtr eventHandler ) override
        {
            std::unique_lock<std::shared_mutex> _coreMutexLock( m_core.coreMutex );

            auto it = findEventHandler( eventHandler );
            if( it != m_core.handlers.end() )
            {
                for( TMyHandlerRunner* oneHandlerRunner : m_handlerRunners )
                {
                    if( it == oneHandlerRunner->currentIt )
                    {
                        ++oneHandlerRunner->currentIt;
                        oneHandlerRunner->wasRemoving = true;
                    }
                }
                m_core.handlers.erase( it );
                return true;
            }
            return false;
        }

    private:

        inline TMyEventHandlerIt findEventHandler( const TMyEventHandlerPtr& eventHandler ) const noexcept
        {
            return std::find_if( m_core.handlers.cbegin(), m_core.handlers.cend(), [ &eventHandler ]( const TMyEventHandlerPtr& oneHandler )
            {
                return ( *oneHandler == *eventHandler );
            } );
        }

        EventCore<TParams...> m_core;
        std::list<TMyHandlerRunner*> m_handlerRunners;
};

template<class ...TParams>
class TCancelableEvent : public TEvent<TParams..., bool&>
{
    using TEventBase = TEvent<TParams..., bool&>;

public:
    bool operator()(TParams... params)
    {
        bool canceled = false;
        TEventBase::operator ()(params..., canceled);
        return !canceled;
    }
};
