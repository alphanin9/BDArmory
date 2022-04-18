﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BDArmory.Control;
using BDArmory.Core;
using UnityEngine;
using BDArmory.Competition.RemoteOrchestration;
using static BDArmory.Competition.RemoteOrchestration.BDAScoreService;

namespace BDArmory.Competition.OrchestrationStrategies
{
    public class RankedFreeForAllStrategy : OrchestrationStrategy
    {
        private BDAScoreService service;
        private BDAScoreClient client;

        public RankedFreeForAllStrategy()
        {
        }

        public IEnumerator Execute(BDAScoreClient client, BDAScoreService service)
        {
            this.client = client;
            this.service = service;
            yield return new WaitForSeconds(1.0f);

            yield return FetchAndExecuteHeat(client.competitionHash, client.activeHeat);
        }

        private IEnumerator FetchAndExecuteHeat(string hash, HeatModel model)
        {
            yield return ExecuteHeat(hash, model);
        }

        private IEnumerator ExecuteHeat(string hash, HeatModel model)
        {
            Debug.Log(string.Format("[BDArmory.BDAScoreService] Running heat {0}/{1}", hash, model.order));

            // orchestrate the match
            service.ClearScores();

            service.status = StatusType.RunningHeat;
            BDACompetitionMode.Instance.StartCompetitionMode(BDArmorySettings.COMPETITION_DISTANCE);
            yield return new WaitForFixedUpdate(); // Give the competition start a frame to get going.

            // start timer coroutine for the duration specified in settings UI
            var duration = Core.BDArmorySettings.COMPETITION_DURATION * 60d;
            var message = "Starting " + (duration > 0 ? "a " + duration.ToString("F0") + "s" : "an unlimited") + " duration competition.";
            Debug.Log("[BDArmory.BDAScoreService]: " + message);
            BDACompetitionMode.Instance.competitionStatus.Add(message);

            // Wait for the competition to actually start.
            yield return new WaitWhile(() => BDACompetitionMode.Instance.competitionStarting || BDACompetitionMode.Instance.sequencedCompetitionStarting);

            if (!BDACompetitionMode.Instance.competitionIsActive)
            {
                message = "Competition failed to start for heat " + hash + ".";
                BDACompetitionMode.Instance.competitionStatus.Add(message);
                Debug.Log("[BDArmory.BDAScoreService]: " + message);
                yield break;
            }

            // Wait for the competition to finish (limited duration and log dumping is handled directly by the competition now).
            yield return new WaitWhile(() => BDACompetitionMode.Instance.competitionIsActive);

            CleanUp();
        }

        public void CleanUp()
        {
            if (BDACompetitionMode.Instance.competitionIsActive) BDACompetitionMode.Instance.StopCompetition(); // Competition is done, so stop it and do the rest of the book-keeping.
        }
    }
}
