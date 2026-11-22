// Observe the normal wave accepted by Match. Preview battles never enter _finishCombat.
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const { Match } = await import(pathToFileURL(resolve(process.env.PLANNER_SIM, 'server/match/Match.js')));
const finish = Match.prototype._finishCombat;
Match.prototype._finishCombat = function (resultOf) {
    return finish.call(this, field => {
        const result = resultOf(field);
        for (const playerId of field.players) {
            const outcome = result.perPlayer[playerId];
            if (!outcome || result.synthetic) throw Error('Missing simulated wave');
            const player = this.players.get(playerId);
            const bonds = player.privateView().bonds.filter(b => b.active);
            appendFileSync(process.env.PLANNER_WAVES, JSON.stringify({
                seed: this.seed, round: this.round, terminal: player.shop.level,
                leaks: outcome.leaked.filter(enemy => enemy.counted !== false).length,
                active: bonds.map(b => b.bondId), top: bonds.filter(b => b.tier >= this.gd.bond(b.bondId).thresholds.length).map(b => b.bondId),
            }) + '\n');
        }
        return result;
    });
};
