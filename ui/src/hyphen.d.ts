declare module 'hyphen/*' {
  const hyphenator: { hyphenateSync: (text: string) => string };
  export default hyphenator;
}
