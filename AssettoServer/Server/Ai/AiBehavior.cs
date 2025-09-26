using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AssettoServer.Network.Tcp;
using AssettoServer.Server.Ai.Splines;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Plugin;
using AssettoServer.Shared.Network.Packets.Outgoing;
using AssettoServer.Shared.Services;
using AssettoServer.Utils;
using Microsoft.Extensions.Hosting;
using Prometheus;
using Serilog;

namespace AssettoServer.Server.Ai;

public class AiBehavior : CriticalBackgroundService, IAssettoServerAutostart
{
    private readonly ACServerConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly AiSpline _spline;
    //private readonly EntryCar.Factory _entryCarFactory;

    private readonly JunctionEvaluator _junctionEvaluator;

    private readonly Gauge _aiStateCountMetric = Metrics.CreateGauge("assettoserver_aistatecount", "Number of AI states");

    private readonly Summary _updateDurationTimer;
    private readonly Summary _obstacleDetectionDurationTimer;

    public AiBehavior(SessionManager sessionManager,
        ACServerConfiguration configuration,
        EntryCarManager entryCarManager,
        IHostApplicationLifetime applicationLifetime,
        //EntryCar.Factory entryCarFactory,
        CSPServerScriptProvider serverScriptProvider, 
        AiSpline spline) : base(applicationLifetime)
    {
        _sessionManager = sessionManager;
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _spline = spline;
        _junctionEvaluator = new JunctionEvaluator(spline, false);
        //_entryCarFactory = entryCarFactory;

        if (_configuration.Extra.AiParams.Debug)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AssettoServer.Server.Ai.ai_debug.lua")!;
            serverScriptProvider.AddScript(stream, "ai_debug.lua");
        }

        _updateDurationTimer = Metrics.CreateSummary("assettoserver_aibehavior_update", "AiBehavior.Update Duration", MetricDefaults.DefaultQuantiles);
        _obstacleDetectionDurationTimer = Metrics.CreateSummary("assettoserver_aibehavior_obstacledetection", "AiBehavior.ObstacleDetection Duration", MetricDefaults.DefaultQuantiles);

        _entryCarManager.ClientConnected += (client, _) =>
        {
            client.ChecksumPassed += OnClientChecksumPassed;
            client.Collision += OnCollision;
        };

        _entryCarManager.ClientDisconnected += OnClientDisconnected;
        _configuration.Extra.AiParams.PropertyChanged += (_, _) => AdjustOverbooking();
    }

    private static void OnCollision(ACTcpClient sender, CollisionEventArgs args)
    {
        if (args.TargetCar?.AiControlled == true)
        {
            var targetAiState = args.TargetCar.GetClosestAiState(sender.EntryCar.Status.Position);
            if (targetAiState.AiState != null && targetAiState.DistanceSquared < 25 * 25)
            {
                Task.Delay(Random.Shared.Next(100, 500)).ContinueWith(_ => targetAiState.AiState.StopForCollision());
            }
        }
    }

    private void OnClientChecksumPassed(ACTcpClient sender, EventArgs args)
    {
        sender.EntryCar.SetAiControl(false);
        AdjustOverbooking();
    }

    private async Task ObstacleDetectionAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var context = _obstacleDetectionDurationTimer.NewTimer();

                for (int i = 0; i < _entryCarManager.EntryCars.Length; i++)
                {
                    var entryCar = _entryCarManager.EntryCars[i];
                    if (entryCar.AiControlled)
                    {
                        entryCar.AiObstacleDetection();
                    }
                }

                if (_configuration.Extra.AiParams.Debug)
                {
                    SendDebugPackets();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in AI obstacle detection");
            }
        }
    }

    private void SendDebugPackets()
    {
        CountedArray<byte> sessionIds = new(_entryCarManager.EntryCars.Length);
        CountedArray<byte> currentSpeeds = new(_entryCarManager.EntryCars.Length);
        CountedArray<byte> targetSpeeds = new(_entryCarManager.EntryCars.Length);
        CountedArray<byte> maxSpeeds = new(_entryCarManager.EntryCars.Length);
        CountedArray<short> closestAiObstacles = new(_entryCarManager.EntryCars.Length);
        foreach (var player in _entryCarManager.ConnectedCars.Values)
        {
            if (player.Client?.HasSentFirstUpdate == false) continue;

            sessionIds.Clear();
            currentSpeeds.Clear();
            targetSpeeds.Clear();
            maxSpeeds.Clear();
            closestAiObstacles.Clear();

            foreach (var car in _entryCarManager.EntryCars)
            {
                if (!car.AiControlled) continue;

                var (aiState, _) = car.GetClosestAiState(player.Status.Position);
                if (aiState == null) continue;

                sessionIds.Add(car.SessionId);
                currentSpeeds.Add((byte)(aiState.CurrentSpeed * 3.6f));
                targetSpeeds.Add((byte)(aiState.TargetSpeed * 3.6f));
                maxSpeeds.Add((byte)(aiState.MaxSpeed * 3.6f));
                closestAiObstacles.Add((short)aiState.ClosestAiObstacleDistance);
            }

            for (int i = 0; i < sessionIds.Count; i += AiDebugPacket.Length)
            {
                var packet = new AiDebugPacket();
                Array.Fill(packet.SessionIds, (byte)255);

                new ArraySegment<byte>(sessionIds.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.SessionIds);
                new ArraySegment<short>(closestAiObstacles.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.ClosestAiObstacles);
                new ArraySegment<byte>(currentSpeeds.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.CurrentSpeeds);
                new ArraySegment<byte>(maxSpeeds.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.MaxSpeeds);
                new ArraySegment<byte>(targetSpeeds.Array, i, Math.Min(AiDebugPacket.Length, sessionIds.Count - i)).CopyTo(packet.TargetSpeeds);

                player.Client?.SendPacket(packet);
            }
        }
    }
    
    private readonly List<EntryCar> _playerCars = new();
    private readonly List<EntryCar> _aiCars = new();
    private readonly List<AiState> _initializedAiStates = new();
    private readonly List<AiState> _uninitializedAiStates = new();
    private readonly List<Vector3> _playerOffsetPositions = new();
    private readonly List<KeyValuePair<AiState, float>> _aiMinDistanceToPlayer = new();
    private readonly List<KeyValuePair<EntryCar, float>> _playerMinDistanceToAi = new();
    private readonly List<EntryCar> _playersToProcess = new();
    private readonly List<EntryCar> _playersToRemove = new();
    private readonly List<int> _sameDirectionLanes = new();
    private readonly List<int> _oppositeDirectionLanes = new();
    private readonly List<AiState> _toPruneAiStates = new();
    private void Update()
    {
        using var context = _updateDurationTimer.NewTimer();

        // === PHASE 1: Reset working collections ===
        _playerCars.Clear();
        _aiCars.Clear();
        _initializedAiStates.Clear();
        _uninitializedAiStates.Clear();
        _playerOffsetPositions.Clear();
        _aiMinDistanceToPlayer.Clear();
        _playerMinDistanceToAi.Clear();

        // === PHASE 2: Categorize all cars (players vs AI) ===
        foreach (var entryCar in _entryCarManager.EntryCars)
        {
            var (currentSplinePointId, _) = _spline.WorldToSpline(entryCar.Status.Position);
            var drivingTheRightWay = Vector3.Dot(_spline.Operations.GetForwardVector(currentSplinePointId), entryCar.Status.Velocity) > 0;

            // Identify players, fully connected, not AFK, and driving the right way
            if (!entryCar.AiControlled  // Players
                && entryCar.Client?.HasSentFirstUpdate == true  // fully connected
                && _sessionManager.ServerTimeMilliseconds - entryCar.LastActiveTime < _configuration.Extra.AiParams.PlayerAfkTimeoutMilliseconds // not AFK
                && (_configuration.Extra.AiParams.TwoWayTraffic || _configuration.Extra.AiParams.WrongWayTraffic || drivingTheRightWay)) // driving the right way
            {
                _playerCars.Add(entryCar);
            }
            else if (entryCar.AiControlled)
            {
                _aiCars.Add(entryCar);
                entryCar.GetInitializedStates(_initializedAiStates, _uninitializedAiStates);
            }
        }

        // If there are no qualifying player cars, there's no need for AI cars.
        if (_playerCars.Count == 0)
        {
            // Despawn all AI cars
            foreach (var aiState in _initializedAiStates)
                aiState.Despawn();
            return;
        }

        _aiStateCountMetric.Set(_initializedAiStates.Count);

        // === PHASE 3: Calculate distances between AI and players ===
        for (int i = 0; i < _initializedAiStates.Count; i++)
        {
            _aiMinDistanceToPlayer.Add(new KeyValuePair<AiState, float>(_initializedAiStates[i], float.MaxValue));
        }

        for (int i = 0; i < _playerCars.Count; i++)
        {
            _playerMinDistanceToAi.Add(new KeyValuePair<EntryCar, float>(_playerCars[i], float.MaxValue));
        }

        // Calculate player offset positions
        for (int i = 0; i < _playerCars.Count; i++)
        {
            if (_playerOffsetPositions.Count <= i)
            {
                var offsetPosition = _playerCars[i].Status.Position;

                // If player is moving, offset their position in the direction of travel by PlayerPositionOffsetMeters
                if (_playerCars[i].Status.Velocity != Vector3.Zero)
                {
                    offsetPosition += Vector3.Normalize(_playerCars[i].Status.Velocity) * _configuration.Extra.AiParams.PlayerPositionOffsetMeters;
                }

                _playerOffsetPositions.Add(offsetPosition);
            }
        }

        // Calculate distance between every AI state and every player
        for (int i = 0; i < _initializedAiStates.Count; i++)
        {
            for (int j = 0; j < _playerCars.Count; j++)
            {
                var distanceSquared = Vector3.DistanceSquared(_initializedAiStates[i].Status.Position, _playerOffsetPositions[j]);

                if (_aiMinDistanceToPlayer[i].Value > distanceSquared)
                {
                    _aiMinDistanceToPlayer[i] = new KeyValuePair<AiState, float>(_initializedAiStates[i], distanceSquared);
                }

                if (_playerMinDistanceToAi[j].Value > distanceSquared)
                {
                    _playerMinDistanceToAi[j] = new KeyValuePair<EntryCar, float>(_playerCars[j], distanceSquared);
                }
            }
        }
        
        // Order AI states by their distance to any player, in descending order.
        _aiMinDistanceToPlayer.Sort((a, b) => b.Value.CompareTo(a.Value));

        // === PHASE 4: Remove unsafe AI states ===
        foreach (var entryCar in _aiCars)
            entryCar.RemoveUnsafeStates(_aiMinDistanceToPlayer);
        _initializedAiStates.RemoveAll(x => !x.Initialized);

        // Add AI cars that are not too close to a player to the uninitialized AI states list
        foreach (var dist in _aiMinDistanceToPlayer)
        {
            // Skip AI cars that are too close to a player
            if (dist.Value < _configuration.Extra.AiParams.PlayerRadiusSquared) continue;

            // Skip AI cars that are protected from despawning
            if (_sessionManager.ServerTimeMilliseconds < dist.Key.SpawnProtectionEnds) continue;

            // Add AI car to the uninitialized AI states list
            _uninitializedAiStates.Add(dist.Key);
        }
        
        // Reorder the player cars by their minimum distance to an AI.
        if (_initializedAiStates.Count > 0 && _playerCars.Count > 0)
        {
            _playerCars.Clear();

            // Order player cars by their distance to an AI.
            // List is sorted in descending order.
            _playerMinDistanceToAi.Sort((a, b) => b.Value.CompareTo(a.Value));

            for (int i = 0; i < _playerMinDistanceToAi.Count; i++)
            {
                _playerCars.Add(_playerMinDistanceToAi[i].Key);
            }
        }

        // Attempt to spawn one AI car per player
        while (_playerCars.Count > 0 && _uninitializedAiStates.Count > 0)
        {
            int spawnPointId = -1;
            while (spawnPointId < 0 && _playerCars.Count > 0)
            {
                var targetPlayerCar = _playerCars.ElementAt(GetRandomWeighted(_playerCars.Count));
                _playerCars.Remove(targetPlayerCar);

                spawnPointId = GetSpawnPoint(targetPlayerCar);
            }

            // targetPlayerCar is not in proximity to the AI spline; skip
            if (spawnPointId < 0)
                continue;

            // spawnPointId has no spline point after it; skip
            if (!_junctionEvaluator.TryNext(spawnPointId, out _))
                continue;

            var previousAi = FindClosestAiState(spawnPointId, false);
            var nextAi = FindClosestAiState(spawnPointId, true);

            foreach (var targetAiState in _uninitializedAiStates)
            {
                if (!targetAiState.CanSpawn(spawnPointId, previousAi, nextAi))
                    continue;

                targetAiState.Teleport(spawnPointId);

                _uninitializedAiStates.Remove(targetAiState);
                break;
            }
        }
    }

    private AiState? FindClosestAiState(int pointId, bool forward)
    {
        var points = _spline.Points;
        float distanceTravelled = 0;
        float searchDistance = 50;
        ref readonly var point = ref points[pointId];
        
        AiState? closestAiState = null;
        while (distanceTravelled < searchDistance && closestAiState == null)
        {
            distanceTravelled += point.Length;
            // TODO reuse this junction evaluator for the newly spawned car
            pointId = forward ? _junctionEvaluator.Next(pointId) : _junctionEvaluator.Previous(pointId);
            if (pointId < 0)
                break;

            point = ref points[pointId];
            
            var slowest = _spline.SlowestAiStates[pointId];
            if (slowest != null)
            {
                closestAiState = slowest;
            }
        }

        return closestAiState;
    }

    private async Task UpdateAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_configuration.Extra.AiParams.AiBehaviorUpdateIntervalMilliseconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                Update();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in AI update");
            }
        }
    }

    private void OnClientDisconnected(ACTcpClient sender, EventArgs args)
    {
        if (sender.EntryCar.AiMode != AiMode.None)
        {
            sender.EntryCar.SetAiControl(true);
            AdjustOverbooking();
        }
    }

    private int GetRandomWeighted(int max)
    {
        // Probabilities for max = 4
        // 0    4/10
        // 1    3/10
        // 2    2/10
        // 3    1/10
            
        int maxRand = max * (max + 1) / 2;
        int rand = Random.Shared.Next(maxRand);
        int target = 0;
        for (int i = max; i < maxRand; i += (i - 1))
        {
            if (rand < i) break;
            target++;
        }

        return target;
    }

    private bool IsPositionSafe(int pointId)
    {
        var ops = _spline.Operations;

        for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
        {
            var entryCar = _entryCarManager.EntryCars[i];
            if (entryCar.AiControlled && !entryCar.IsPositionSafe(pointId))
            {
                return false;
            }

            if (entryCar.Client?.HasSentFirstUpdate == true
                && Vector3.DistanceSquared(entryCar.Status.Position, ops.Points[pointId].Position) < _configuration.Extra.AiParams.SpawnSafetyDistanceToPlayerSquared)
            {
                return false;
            }
        }

        return true;
    }

    private int GetSpawnPoint(EntryCar playerCar)
    {
        var result = _spline.WorldToSpline(playerCar.Status.Position);
        var ops = _spline.Operations;


        if (result.PointId < 0 || ops.Points[result.PointId].NextId < 0) return -1;
        
        int direction = Vector3.Dot(ops.GetForwardVector(result.PointId), playerCar.Status.Velocity) > 0 ? 1 : -1;
        
        // Do not not spawn if a player is too far away from the AI spline, e.g. in pits or in a part of the map without traffic
        if (result.DistanceSquared > _configuration.Extra.AiParams.MaxPlayerDistanceToAiSplineSquared)
        {
            return -1;
        }
        
        int spawnDistance = Random.Shared.Next(_configuration.Extra.AiParams.MinSpawnDistancePoints, _configuration.Extra.AiParams.MaxSpawnDistancePoints);
        var spawnPointId = _junctionEvaluator.Traverse(result.PointId, spawnDistance * direction);

        if (spawnPointId >= 0)
        {
            spawnPointId = SelectLaneForPlayer(spawnPointId, playerCar);
        }
        
        if (spawnPointId >= 0 && ops.Points[spawnPointId].NextId >= 0)
        {
            direction = Vector3.Dot(ops.GetForwardVector(spawnPointId), playerCar.Status.Velocity) > 0 ? 1 : -1;
        }

        int maxSearchDistance = _configuration.Extra.AiParams.MaxSpawnDistancePoints - spawnDistance;
        int totalDistanceTraversed = 0;

        while (spawnPointId >= 0 && !IsPositionSafe(spawnPointId))
        {
            // Check if we've searched too far from the original spawnPointId
            if (totalDistanceTraversed > maxSearchDistance)
            {
                Log.Verbose("GetSpawnPoint could not find safe spawn position within {MaxDistance} points", maxSearchDistance);
                return -1;
            }

            int previousPointId = spawnPointId;
            spawnPointId = _junctionEvaluator.Traverse(spawnPointId, direction * 5);

            // Calculate actual distance traversed
            if (spawnPointId >= 0)
            {
                totalDistanceTraversed += Math.Abs(spawnPointId - previousPointId);
            }
        }
        
        if (spawnPointId >= 0)
        {
            spawnPointId = SelectLaneForPlayer(spawnPointId, playerCar);
        }

        return spawnPointId;
    }

    private int SelectLaneForPlayer(int spawnPointId, EntryCar playerCar)
    {
        // If prioritization is disabled, use random lane selection
        if (!_configuration.Extra.AiParams.PrioritizePlayerTraffic || !_configuration.Extra.AiParams.TwoWayTraffic)
        {
            return _spline.RandomLane(spawnPointId);
        }

        // Get all available lanes for this spawn point
        var availableLanes = _spline.GetLanes(spawnPointId);
        if (availableLanes.Length <= 1)
        {
            return _spline.RandomLane(spawnPointId);
        }

        // Determine player's current lane direction using the same pattern as GetLanes
        var playerSplineResult = _spline.WorldToSpline(playerCar.Status.Position);
        if (playerSplineResult.PointId < 0)
        {
            return _spline.RandomLane(spawnPointId);
        }

        // Clear and reuse the existing lists to avoid allocations
        _sameDirectionLanes.Clear();
        _oppositeDirectionLanes.Clear();

        // Separate lanes by direction using the existing IsSameDirection pattern
        foreach (var laneId in availableLanes)
        {
            if (_spline.Operations.IsSameDirection(playerSplineResult.PointId, laneId))
            {
                _sameDirectionLanes.Add(laneId);
            }
            else
            {
                _oppositeDirectionLanes.Add(laneId);
            }
        }

        // Apply weighted selection based on configuration
        if (_sameDirectionLanes.Count > 0 && _oppositeDirectionLanes.Count > 0)
        {
            // Both directions available - use weighted random selection
            float sameDirectionProbability = _configuration.Extra.AiParams.SameDirectionTrafficProbability;
            if (Random.Shared.NextDouble() < sameDirectionProbability)
            {
                return _sameDirectionLanes[Random.Shared.Next(_sameDirectionLanes.Count)];
            }
            else
            {
                return _oppositeDirectionLanes[Random.Shared.Next(_oppositeDirectionLanes.Count)];
            }
        }
        else if (_sameDirectionLanes.Count > 0)
        {
            // Only same-direction lanes available
            return _sameDirectionLanes[Random.Shared.Next(_sameDirectionLanes.Count)];
        }
        else if (_oppositeDirectionLanes.Count > 0)
        {
            // Only opposite-direction lanes available
            return _oppositeDirectionLanes[Random.Shared.Next(_oppositeDirectionLanes.Count)];
        }

        // Final fallback to random selection
        return _spline.RandomLane(spawnPointId);
    }

    private void AdjustOverbooking()
    {
        int playerCount = _entryCarManager.EntryCars.Count(car => car.Client != null && car.Client.IsConnected);
        var aiSlots = _entryCarManager.EntryCars.Where(car => car.Client == null && car.AiControlled).ToList(); // client null check is necessary here so that slots where someone is connecting don't count

        if (aiSlots.Count == 0)
        {
            Log.Debug("AI Slot overbooking update - no AI slots available");
            return;
        }
            
        int targetAiCount = Math.Min(playerCount * Math.Min((int)Math.Round(_configuration.Extra.AiParams.AiPerPlayerTargetCount * _configuration.Extra.AiParams.TrafficDensity), aiSlots.Count), _configuration.Extra.AiParams.MaxAiTargetCount);

        int overbooking = targetAiCount / aiSlots.Count;
        int rest = targetAiCount % aiSlots.Count;
            
        Log.Debug("AI Slot overbooking update - No. players: {NumPlayers} - No. AI Slots: {NumAiSlots} - Target AI count: {TargetAiCount} - Overbooking: {Overbooking} - Rest: {Rest}", 
            playerCount, aiSlots.Count, targetAiCount, overbooking, rest);

        for (int i = 0; i < aiSlots.Count; i++)
        {
            aiSlots[i].SetAiOverbooking(i < rest ? overbooking + 1 : overbooking);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = UpdateAsync(stoppingToken);
        _ = ObstacleDetectionAsync(stoppingToken);

        return Task.CompletedTask;
    }

}
