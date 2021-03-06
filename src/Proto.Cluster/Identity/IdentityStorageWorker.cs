using Proto.Utils;

namespace Proto.Cluster.Identity
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Utils;

    internal class IdentityStorageWorker : IActor
    {
        private static readonly ConcurrentSet<string> StaleMembers = new();

        private readonly Cluster _cluster;
        private readonly ILogger _logger = Log.CreateLogger<IdentityStorageWorker>();
        private readonly IdentityStorageLookup _lookup;
        private readonly MemberList _memberList;
        private readonly IIdentityStorage _storage;

        private readonly Dictionary<ClusterIdentity, Task<PID?>> _inProgress = new();

        private readonly ShouldThrottle _shouldThrottle;

        public IdentityStorageWorker(IdentityStorageLookup storageLookup)
        {
            _shouldThrottle = Throttle.Create(
                10,
                TimeSpan.FromSeconds(5),
                i => _logger.LogInformation("Throttled {LogCount} IdentityStorageWorker logs.", i)
            );
            
            _cluster = storageLookup.Cluster;
            _memberList = storageLookup.MemberList;
            _lookup = storageLookup;
            _storage = storageLookup.Storage;
        }

        public Task ReceiveAsync(IContext context)
        {
            if (context.Message is not GetPid msg) return Task.CompletedTask;
            if (context.Sender == null)
            {
                _logger.LogCritical("No sender in GetPid request");
                return Task.CompletedTask;
            }

            var clusterIdentity = msg.ClusterIdentity;
            var ct = msg.CancellationToken;
            
            if (ct.IsCancellationRequested)
            {
                //_logger.LogError("CT already timed out....");
                return Task.CompletedTask;
            }
            
            if (_cluster.PidCache.TryGet(clusterIdentity, out var existing))
            {
                _logger.LogDebug("Found {ClusterIdentity} in pidcache", clusterIdentity);
                context.Respond(new PidResult
                    {
                        Pid = existing
                    }
                );
                return Task.CompletedTask;
            }

            try
            {
                if (_inProgress.TryGetValue(clusterIdentity, out Task<PID?> getPid) && getPid.IsCompleted)
                {
                    try
                    {
                        if (getPid.IsCompletedSuccessfully)
                        {
                            var pid = getPid.Result;
                            if (pid != null)
                            {
                                _cluster.PidCache.TryAdd(clusterIdentity, pid);
                            }

                            context.Respond(new PidResult
                                {
                                    Pid = pid
                                }
                            );
                            return Task.CompletedTask;
                        }
                        else
                        {
                            if (_shouldThrottle().IsOpen())
                                _logger.LogWarning(getPid.Exception, "GetWithGlobalLock for {ClusterIdentity} failed", clusterIdentity);
                        }
                    }
                    finally
                    {
                        _inProgress.Remove(clusterIdentity);
                    }
                }

                if (getPid == null)
                {
                    getPid = GetWithGlobalLock(context.Sender!, clusterIdentity, CancellationToken.None);
                    _inProgress[clusterIdentity] = getPid;
                }

                context.ReenterAfter(getPid, task =>
                    {
                        try
                        {
                            context.Respond(new PidResult
                                {
                                    Pid = task.Result
                                }
                            );
                            return Task.CompletedTask;
                        }
                        catch (Exception x)
                        {
                            if (_shouldThrottle().IsOpen())
                                _logger.LogError(x, "Identity worker crashed in reentrant context: {Id}", context.Self);
                            throw;
                        }
                        finally
                        {
                            _inProgress.Remove(clusterIdentity);
                        }
                    }
                );

                return Task.CompletedTask;
            }
            catch (Exception x)
            {
                _logger.LogError(x, "Identity worker crashed {Id}", context.Self);
                throw;
            }
        }

        private async Task<PID?> GetWithGlobalLock(PID sender, ClusterIdentity clusterIdentity, CancellationToken ct)
        {
            try
            {
                var activation = await _storage.TryGetExistingActivationAsync(clusterIdentity, ct);
                //we got an existing activation, use this
                if (activation != null)
                {
                    var existingPid = await ValidateAndMapToPid(clusterIdentity, activation);
                    if (existingPid != null)
                    {
                        return existingPid;
                    }
                }

                //are there any members that can spawn this kind?
                //if not, just bail out
                var activator = _memberList.GetActivator(clusterIdentity.Kind, sender.Address);
                if (activator == null) return null;

                //try to acquire global lock
                var spawnLock = await _storage.TryAcquireLockAsync(clusterIdentity, ct);


                //we didn't get the lock, wait for activation to complete
                if (spawnLock == null)
                    return await ValidateAndMapToPid(
                        clusterIdentity,
                        await _storage.WaitForActivationAsync(clusterIdentity, ct)
                    );

                //we have the lock, spawn and return
                var pid = await SpawnActivationAsync(activator, spawnLock, ct);

                return pid;
            }
            catch (Exception e)
            {
                if (_cluster.System.Shutdown.IsCancellationRequested)
                {
                    return null;
                }
                if (_shouldThrottle().IsOpen())
                    _logger.LogError(e, "Failed to get PID for {ClusterIdentity}", clusterIdentity);
                return null;
            }
            
        }

        private async Task<PID?> SpawnActivationAsync(Member activator, SpawnLock spawnLock, CancellationToken ct)
        {
            //we own the lock
            _logger.LogDebug("Storing placement lookup for {Identity} {Kind}", spawnLock.ClusterIdentity.Identity,
                spawnLock.ClusterIdentity.Kind
            );

            var remotePid = _lookup.RemotePlacementActor(activator.Address);
            var req = new ActivationRequest
            {
                ClusterIdentity = spawnLock.ClusterIdentity,
                RequestId = spawnLock.LockId
            };

            try
            {
                var resp = ct == CancellationToken.None
                    ? await _cluster.System.Root.RequestAsync<ActivationResponse>(remotePid, req,
                        _cluster.Config!.TimeoutTimespan
                    )
                    : await _cluster.System.Root.RequestAsync<ActivationResponse>(remotePid, req, ct);

                if (resp.Pid != null)
                {
                    _cluster.PidCache.TryAdd(spawnLock.ClusterIdentity, resp.Pid!);
                    return resp.Pid;
                }
            }
            //TODO: decide if we throw or return null
            catch (TimeoutException)
            {
                _logger.LogDebug("Remote PID request timeout {@Request}", req);
            }
            catch (Exception e)
            {
                if (!_cluster.System.Shutdown.IsCancellationRequested && _shouldThrottle().IsOpen() && _memberList.ContainsMemberId(activator.Id))
                    _logger.LogError(e, "Error occured requesting remote PID {@Request}", req);
            }

            //Clean up our mess..
            await _storage.RemoveLock(spawnLock, ct);
            return null;
        }


        private async Task<PID?> ValidateAndMapToPid(ClusterIdentity clusterIdentity, StoredActivation? activation)
        {
            if (activation?.Pid == null)
            {
                return null;
            }

            //TODO: can  activation.MemberId == null ever happen?
            var memberExists = activation.MemberId == null || _memberList.ContainsMemberId(activation.MemberId);
            if (memberExists) return activation.Pid;

            if (StaleMembers.TryAdd(activation.MemberId!))
            {
                _logger.LogWarning(
                    "Found placement lookup for {ClusterIdentity}, but Member {MemberId} is not part of cluster, dropping stale entries",
                    clusterIdentity, activation.MemberId
                );
            }

            //let all requests try to remove, but only log on the first occurrence
            await _storage.RemoveMemberIdAsync(activation.MemberId!, CancellationToken.None);
            return null;

        }
    }
}