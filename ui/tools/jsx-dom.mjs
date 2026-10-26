/* Build-time JSX -> DOM transform (dev only, never shipped). See README.md for the authoring rules.
   An esbuild plugin runs every .tsx file through a TypeScript custom transformer that replaces each JSX
   tree with straight-line DOM code (createElement / createElementNS, attribute sets, append, listeners),
   prints the result as plain TypeScript and hands it to esbuild with the `ts` loader. */
import { readFile } from "node:fs/promises";
import path from "node:path";
import ts from "typescript";

const f = ts.factory;
const SVG_NS = "http://www.w3.org/2000/svg";
const SVG_TAGS = new Set([
  "svg",
  "g",
  "path",
  "circle",
  "ellipse",
  "rect",
  "line",
  "polyline",
  "polygon",
]);
const LABEL_TEXT = "__lText";
const LABEL_ATTR = "__lAttr";

/* JSX text whitespace, the same rule as React/Babel: lines are trimmed, blank lines dropped, joined by a space. */
const cleanText = (raw) => {
  const lines = raw.split(/\r\n|\n|\r/);
  let lastNonEmpty = 0;
  for (const [i, line] of lines.entries()) {
    if (/[^ \t]/.test(line)) {
      lastNonEmpty = i;
    }
  }
  let out = "";
  for (const [i, line] of lines.entries()) {
    let s = line.replace(/\t/g, " ");
    if (i !== 0) {
      s = s.replace(/^ +/, "");
    }
    if (i !== lines.length - 1) {
      s = s.replace(/ +$/, "");
    }
    if (s) {
      out += i === lastNonEmpty ? s : `${s} `;
    }
  }
  return out;
};

const isJsx = (node) =>
  ts.isJsxElement(node) ||
  ts.isJsxSelfClosingElement(node) ||
  ts.isJsxFragment(node);
/* Parentheses and type assertions (`<div /> as HTMLDivElement`) around a JSX root; types are erased anyway. */
const unwrap = (node) => {
  let n = node;
  while (
    n &&
    (ts.isParenthesizedExpression(n) ||
      ts.isAsExpression(n) ||
      ts.isSatisfiesExpression(n) ||
      ts.isNonNullExpression(n))
  ) {
    n = n.expression;
  }
  return n;
};
const call = (target, method, args) =>
  f.createCallExpression(
    f.createPropertyAccessExpression(target, method),
    undefined,
    args,
  );
const stmt = (expr) => f.createExpressionStatement(expr);
const str = (s) => f.createStringLiteral(s);

const createTransformer = (sf, i18nSpecifier) => (ctx) => {
  let usesText = false;
  let usesAttr = false;

  const fail = (node, message) => {
    const { line, character } = sf.getLineAndCharacterOfPosition(
      node.getStart(sf),
    );
    throw new Error(`${sf.fileName}:${line + 1}:${character + 1}: ${message}`);
  };

  const attrName = (attr) =>
    ts.isJsxNamespacedName(attr.name)
      ? `${attr.name.namespace.text}:${attr.name.name.text}`
      : attr.name.text;
  const attrValue = (attr) => {
    const init = attr.initializer;
    if (!init) {
      return null;
    }
    if (ts.isStringLiteral(init)) {
      return str(init.text);
    }
    if (ts.isJsxExpression(init) && init.expression) {
      return ts.visitNode(init.expression, visit);
    }
    return fail(attr, `unsupported value for "${attrName(attr)}"`);
  };
  const labelKey = (attr) => {
    const value = attrValue(attr);
    if (!value) {
      fail(attr, `"${attrName(attr)}" needs a label key`);
    }
    return value;
  };

  /* Emits the statements that build `node` into `out`; returns the identifier holding the built node. */
  const build = (node, out) => {
    if (ts.isJsxFragment(node)) {
      fail(node, "fragments are not supported; return one root element");
    }
    const open = ts.isJsxElement(node) ? node.openingElement : node;
    const children = ts.isJsxElement(node) ? node.children : [];
    const tagNode = open.tagName;
    const id = f.createUniqueName("e");
    const refs = [];
    const isComponent =
      !ts.isIdentifier(tagNode) || /^[A-Z]/.test(tagNode.text);

    if (isComponent) {
      const props = [];
      for (const attr of open.attributes.properties) {
        if (
          !ts.isJsxAttribute(attr) ||
          ts.isJsxNamespacedName(attr.name) ||
          attr.name.text === "ref"
        ) {
          fail(
            attr,
            "components take plain attributes only (no spread, ref or l:)",
          );
        }
        const value = attrValue(attr);
        const key = /^[A-Za-z_$][\w$]*$/.test(attr.name.text)
          ? f.createIdentifier(attr.name.text)
          : str(attr.name.text);
        props.push(f.createPropertyAssignment(key, value ?? f.createTrue()));
      }
      for (const child of children) {
        if (!ts.isJsxText(child) || cleanText(child.text)) {
          fail(child, "components take no children");
        }
      }
      const tag = ts.visitNode(tagNode, visit);
      out.push(
        f.createVariableStatement(
          undefined,
          f.createVariableDeclarationList(
            [
              f.createVariableDeclaration(
                id,
                undefined,
                undefined,
                f.createCallExpression(tag, undefined, [
                  f.createObjectLiteralExpression(props),
                ]),
              ),
            ],
            ts.NodeFlags.Const,
          ),
        ),
      );
      return id;
    }

    const tag = tagNode.text;
    const svg = SVG_TAGS.has(tag);
    const create = svg
      ? call(f.createIdentifier("document"), "createElementNS", [
          str(SVG_NS),
          str(tag),
        ])
      : call(f.createIdentifier("document"), "createElement", [str(tag)]);
    out.push(
      f.createVariableStatement(
        undefined,
        f.createVariableDeclarationList(
          [f.createVariableDeclaration(id, undefined, undefined, create)],
          ts.NodeFlags.Const,
        ),
      ),
    );

    const items = [];
    for (const attr of open.attributes.properties) {
      if (!ts.isJsxAttribute(attr)) {
        fail(attr, "spread attributes are not supported");
      }
      const name = attrName(attr);
      if (name === "ref") {
        refs.push(attrValue(attr));
      } else if (name === "l:text") {
        usesText = true;
        items.push(
          f.createCallExpression(f.createIdentifier(LABEL_TEXT), undefined, [
            labelKey(attr),
          ]),
        );
      } else if (name.startsWith("l:")) {
        usesAttr = true;
        out.push(
          stmt(
            f.createCallExpression(f.createIdentifier(LABEL_ATTR), undefined, [
              id,
              str(name.slice(2)),
              labelKey(attr),
            ]),
          ),
        );
      } else if (
        name.startsWith("on") &&
        attr.initializer &&
        !ts.isStringLiteral(attr.initializer)
      ) {
        out.push(
          stmt(
            call(id, "addEventListener", [str(name.slice(2)), attrValue(attr)]),
          ),
        );
      } else if (name === "class" && !svg) {
        out.push(
          stmt(
            f.createAssignment(
              f.createPropertyAccessExpression(id, "className"),
              attrValue(attr) ?? str(""),
            ),
          ),
        );
      } else {
        out.push(
          stmt(
            call(id, "setAttribute", [str(name), attrValue(attr) ?? str("")]),
          ),
        );
      }
    }

    for (const child of children) {
      if (ts.isJsxText(child)) {
        if (child.text.includes("&")) {
          fail(child, "HTML entities are not supported in JSX text");
        }
        const text = cleanText(child.text);
        if (text) {
          items.push(str(text));
        }
      } else if (ts.isJsxExpression(child)) {
        if (child.dotDotDotToken) {
          fail(child, "spread children are not supported");
        }
        if (child.expression) {
          items.push(ts.visitNode(child.expression, visit));
        }
      } else {
        items.push(build(child, out));
      }
    }
    if (items.length === 1 && ts.isStringLiteral(items[0])) {
      out.push(
        stmt(
          f.createAssignment(
            f.createPropertyAccessExpression(id, "textContent"),
            items[0],
          ),
        ),
      );
    } else if (items.length) {
      out.push(stmt(call(id, "append", items)));
    }
    for (const target of refs) {
      out.push(stmt(f.createAssignment(target, id)));
    }
    return id;
  };

  /* A JSX tree in expression position: arrow IIFE, or the enclosing statement list when there is one. */
  const inStatementList = (node) =>
    ts.isBlock(node.parent) ||
    ts.isSourceFile(node.parent) ||
    ts.isCaseClause(node.parent) ||
    ts.isDefaultClause(node.parent);
  const visit = (node) => {
    if (
      ts.isReturnStatement(node) &&
      node.expression &&
      isJsx(unwrap(node.expression)) &&
      inStatementList(node)
    ) {
      const out = [];
      const id = build(unwrap(node.expression), out);
      return [...out, f.createReturnStatement(id)];
    }
    if (
      ts.isVariableStatement(node) &&
      node.declarationList.declarations.length === 1 &&
      inStatementList(node)
    ) {
      const decl = node.declarationList.declarations[0];
      if (decl.initializer && isJsx(unwrap(decl.initializer))) {
        const out = [];
        const id = build(unwrap(decl.initializer), out);
        return [
          ...out,
          f.updateVariableStatement(
            node,
            node.modifiers,
            f.updateVariableDeclarationList(node.declarationList, [
              f.updateVariableDeclaration(
                decl,
                decl.name,
                decl.exclamationToken,
                decl.type,
                id,
              ),
            ]),
          ),
        ];
      }
    }
    if (
      ts.isExpressionStatement(node) &&
      ts.isBinaryExpression(node.expression) &&
      node.expression.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
      isJsx(unwrap(node.expression.right)) &&
      inStatementList(node)
    ) {
      const out = [];
      const id = build(unwrap(node.expression.right), out);
      return [
        ...out,
        stmt(f.createAssignment(ts.visitNode(node.expression.left, visit), id)),
      ];
    }
    if (ts.isArrowFunction(node) && isJsx(unwrap(node.body))) {
      const out = [];
      const id = build(unwrap(node.body), out);
      return f.updateArrowFunction(
        node,
        node.modifiers,
        node.typeParameters,
        ts.visitNodes(node.parameters, visit),
        node.type,
        node.equalsGreaterThanToken,
        f.createBlock([...out, f.createReturnStatement(id)], true),
      );
    }
    if (isJsx(node)) {
      const out = [];
      const id = build(node, out);
      const fn = f.createArrowFunction(
        undefined,
        undefined,
        [],
        undefined,
        undefined,
        f.createBlock([...out, f.createReturnStatement(id)], true),
      );
      return f.createCallExpression(
        f.createParenthesizedExpression(fn),
        undefined,
        [],
      );
    }
    return ts.visitEachChild(node, visit, ctx);
  };

  return (file) => {
    const result = ts.visitEachChild(file, visit, ctx);
    if (!usesText && !usesAttr) {
      return result;
    }
    const names = [];
    if (usesText) {
      names.push(
        f.createImportSpecifier(
          false,
          f.createIdentifier("bindText"),
          f.createIdentifier(LABEL_TEXT),
        ),
      );
    }
    if (usesAttr) {
      names.push(
        f.createImportSpecifier(
          false,
          f.createIdentifier("bindAttr"),
          f.createIdentifier(LABEL_ATTR),
        ),
      );
    }
    const imp = f.createImportDeclaration(
      undefined,
      f.createImportClause(false, undefined, f.createNamedImports(names)),
      str(i18nSpecifier),
    );
    return f.updateSourceFile(result, [imp, ...result.statements]);
  };
};

/* Transforms one .tsx source to TypeScript without JSX. `i18nModule`: absolute path of the module exporting
   bindText(key) and bindAttr(el, attr, key), imported automatically when `l:` attributes are used. */
export const transformJsx = (source, fileName, i18nModule) => {
  const sf = ts.createSourceFile(
    fileName,
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
  let spec = path
    .relative(path.dirname(fileName), i18nModule)
    .replace(/\\/g, "/")
    .replace(/\.tsx?$/, "");
  if (!spec.startsWith(".")) {
    spec = `./${spec}`;
  }
  const result = ts.transform(sf, [createTransformer(sf, spec)]);
  const code = ts.createPrinter().printFile(result.transformed[0]);
  result.dispose();
  return code;
};

export const jsxDomPlugin = ({ i18nModule }) => ({
  name: "jsx-dom",
  setup(build) {
    build.onLoad({ filter: /\.tsx$/ }, async (args) => {
      const source = await readFile(args.path, "utf8");
      return {
        contents: transformJsx(source, args.path, i18nModule),
        loader: "ts",
        resolveDir: path.dirname(args.path),
      };
    });
  },
});
