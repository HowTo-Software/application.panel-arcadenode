def number($field):
    if type == "string" and test("^[0-9]+([.][0-9]+)?$") then tonumber
    else error("\($field) must be numeric") end;
def integer($field):
    number($field) | if . == floor then . else error("\($field) must be an integer") end;
def port($field):
    integer($field) | if . >= 1 and . <= 65535 then . else error("\($field) is outside the port range") end;
def boolean($field):
    if . == "true" then true elif . == "false" then false else error("\($field) must be true or false") end;

env as $settings
| (($settings.SERVER_PORT // $settings.GAME_PORT) | port("Game port")) as $gamePort
| ($settings.A2S_PORT | port("A2S_PORT")) as $queryPort
| ($settings.MAX_PLAYERS | integer("MAX_PLAYERS")) as $players
| if $gamePort == $queryPort then error("The game and A2S listeners need separate ports") else
    {
        bindAddress: "0.0.0.0",
        bindPort: $gamePort,
        publicPort: $gamePort,
        a2s: {address: "0.0.0.0", port: $queryPort},
        game: {
            name: $settings.SERVER_NAME,
            password: ($settings.SERVER_PASSWORD // ""),
            passwordAdmin: $settings.ADMIN_PASSWORD,
            admins: [],
            scenarioId: ($settings.SCENARIO_ID |
                if startswith("\"") and endswith("\"") then .[1:-1] else . end),
            maxPlayers: $players,
            visible: ($settings.VISIBLE | boolean("VISIBLE")),
            crossPlatform: ($settings.CROSSPLATFORM | boolean("CROSSPLATFORM")),
            supportedPlatforms: ["PLATFORM_PC", "PLATFORM_XBL"],
            gameProperties: {
                serverMaxViewDistance: ($settings.SERVER_MAX_VIEW_DISTANCE | number("SERVER_MAX_VIEW_DISTANCE")),
                serverMinGrassDistance: ($settings.SERVER_MIN_GRASS_DISTANCE | number("SERVER_MIN_GRASS_DISTANCE")),
                networkViewDistance: ($settings.NETWORK_VIEW_DISTANCE | number("NETWORK_VIEW_DISTANCE")),
                disableThirdPerson: (($settings.DISABLE_THIRD_PERSON // "false") | boolean("DISABLE_THIRD_PERSON")),
                fastValidation: true,
                battlEye: ($settings.BATTLYE | boolean("BATTLYE")),
                VONDisableUI: false,
                VONDisableDirectSpeechUI: false,
                missionHeader: {m_iPlayerCount: $players, m_eEditableGameFlags: 6, m_eDefaultGameFlags: 6}
            },
            mods: (($settings.MODS // "") | split(",") | map(gsub("^\\s+|\\s+$"; "") |
                select(length > 0) | {modId: ., name: "", version: ""}))
        },
        operating: {lobbyPlayerSynchronise: true, disableCrashReporter: false}
    }
end
