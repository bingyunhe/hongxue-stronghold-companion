async function runGuideCommand(command, args) {
    if (location.origin !== "https://game.skland.com") throw Error("请先完成官网登录。");
    // Use the same guide client already loaded by the official page. Its browser profile owns authentication.
    const assets = () => performance.getEntriesByType("resource")
        .filter(entry => entry.name.startsWith("https://assets.skland.com/_static_assets/game-tools/"))
        .map(entry => new URL(entry.name));
    let entry;
    for (let i = 0; i < 60; i++) {
        entry = assets().find(url => /\/entry_index-[\w-]+\.js$/.test(url.pathname));
        if (entry) break;
        await new Promise(resolve => setTimeout(resolve, 250));
    }
    if (!entry) throw Error("官网攻略模块未加载，请重新连接。");
    const account = (await import(entry.href)).n;
    if (typeof account?.ready !== "function") throw Error("官网登录组件已更新。");
    await account.ready();
    if (command === "login") {
        if (!account.isLogin) await account.login();
        return true;
    }
    if (!navigator.onLine) throw Error("网络未连接。");
    const user = account.isLogin && (await account.userInfo)?.user;
    if (account.isLogin !== false && user?.id == null) throw Error("暂时无法读取森空岛账号。");
    if (command === "account") return user ? { id: String(user.id), name: user.nickname } : null;
    if (!user || String(user.id) !== args.accountId)
        throw Object.assign(Error("当前账号已变化，请重新连接森空岛。"), { name: "GuideAccountChanged" });
    let client;
    for (const url of assets().filter(url => /\/dist-[\w-]+\.js$/.test(url.pathname))) {
        const module = await import(url.href);
        client = Object.values(module).find(value => typeof value?.getMyCollectedList === "function");
        if (client) break;
    }
    if (!client) throw Error("官网攻略组件已更新或尚未加载。");
    if (command === "list") {
        const info = await client.getStaticInfo();
        const query = { seasonId: args.season || info.latestSeasonId, pageSize: 10, pageToken: args.cursor || "", sortType: 1 };
        if (args.titleKeyword) query.titleKeyword = args.titleKeyword;
        if (args.seasonBondId) query.seasonBondId = args.seasonBondId;
        if (args.regularBondIds.length) query.regularBondIds = args.regularBondIds;
        if (args.strategyId) query.strategyId = args.strategyId;
        const page = args.section === "collected" ? await client.getMyCollectedList(query)
            : args.section === "published" ? await client.getMyGuideList(query) : await client.getGuideList(query);
        // Both browsing paths feed the same catalogue and lineup importer.
        const mapValues = (map, convert) => Object.fromEntries(Object.entries(map).map(([id, value]) => [id, convert(value)]));
        const seasonResourceMap = mapValues(page.seasonResourceMap, season => ({
            versionResourceMap: mapValues(season.versionResourceMap, version => ({
                chars: mapValues(version.operatorMap, op => ({ ...op, shopLevel: op.tier,
                    skills: op.skills.map(s => ({...s, skillId: s.id})),
                    modules: op.modules.map(m => ({...m, moduleId: m.id})) })),
                items: mapValues(version.equipMap, item => ({...item, effectDesc: item.description})),
                strategies: version.strategyMap, bonds: version.factionMap, richText: version.richTextDataMap
            }))
        }));
        const list = page.list.filter(item => !item.isInvalid).map(({guide, relation, stats, season, poster}) => ({
            invalid: 0, rel: relation, rts: stats, seasonTag: season, poster,
            guide: {...guide, guideId: guide.id, publishAtTs: Math.floor(guide.publishAt / 1000),
                tabs: guide.tabs.map(tab => ({
                    tabType: tab.type === "FORMATION" ? 1 : tab.type === "ROUND" ? 2 : 0,
                    startRound: tab.startRound, endRound: tab.endRound, operationIdea: tab.description,
                    activeBonds: tab.activeFactions ? {seasonBondIds: tab.activeFactions.coreFactionIds, regularBondIds: tab.activeFactions.additionalFactionIds} : null,
                    recChars: tab.operators.map(op => ({charId: op.operatorId, mergeLevel: op.eliteLevel, skillId: op.skillId, moduleId: op.moduleId})),
                    recItems: tab.equips.map(eq => ({itemId: eq.equipId}))
                }))}
        }));
        return {list, seasonResourceMap, pageToken: page.pageToken, hasMore: page.hasMore};
    }
    if (command === "reaction") {
        const result = args.kind === "collected"
            ? await (args.active ? client.collectGuide(args.id) : client.cancelCollectGuide(args.id))
            : await (args.active ? client.likeGuide(args.id) : client.cancelLikeGuide(args.id));
        if (result.code !== 0) throw Error(result.message || "官网未接受该操作。");
        return true;
    }
    if (command === "prepare" || command === "publish") {
        const access = await client.canCreateGuide();
        if (!access.canCreate) throw Error(access.reason || "此账号暂无发布资格，请核对绑定角色和攻略站发布规则。");
        const info = await client.getStaticInfo();
        const idFor = (map, name) => {
            const matches = Object.values(map).filter(value => value.name === name);
            if (matches.length !== 1) throw Error("无法对应当前赛季资源：" + name);
            return matches[0].id;
        };
        const tabs = args.draft.tabs.map(stage => ({
            type: stage.final ? "FORMATION" : "ROUND", startRound: stage.start || 0, endRound: stage.end || 0,
            operators: stage.operators.map(op => ({ operatorId: idFor(info.operatorMap, op.name), eliteLevel: op.elite ? 1 : 0,
                skillId: op.skill, moduleId: op.module })),
            equips: stage.gear.map(name => ({ equipId: idFor(info.equipMap, name) })), description: stage.notes
        }));
        for (const id of args.draft.strategyIds)
            if (!info.strategyMap[id]) throw Error("此策略不属于当前赛季：" + id);
        if (command === "prepare") return tabs.map(tab => client.calculateActiveFactions(
            tab.operators.map(op => ({ id: op.operatorId, eliteLevel: op.eliteLevel })), undefined, true)
            .map(faction => info.factionMap[faction.id].name));
        const result = await client.createGuide({ title: args.draft.title, applicableScope: args.draft.scope,
            strategyIds: args.draft.strategyIds, tabs });
        return { guideId: String(result.guideId || "") };
    }
    throw Error("不支持的攻略操作。");
}
