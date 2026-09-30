export type RulesStackParamList = {
  RulesList: undefined;
  RuleEditor: { id?: number } | undefined;
};

export type RootTabsParamList = {
  Dashboard: undefined;
  Generation: undefined;
  Sales: undefined;
  Devices: undefined;
  More: undefined;
};

export type MoreStackParamList = RulesStackParamList & {
  MoreHome: undefined;
  History: undefined;
  Settings: undefined;
  Subscription: undefined;
};
