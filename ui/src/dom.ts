export const setText = (el: Element, s: string): void => {
  if (el.textContent !== s) {
    el.textContent = s;
  }
};

export const setHidden = (
  el: HTMLElement | SVGElement,
  hidden: boolean,
): void => {
  if (el.hasAttribute("hidden") !== hidden) {
    el.toggleAttribute("hidden", hidden);
  }
};

export const setAttr = (
  el: Element,
  name: string,
  value: string | null,
): void => {
  if (value === null) {
    el.removeAttribute(name);
  } else if (el.getAttribute(name) !== value) {
    el.setAttribute(name, value);
  }
};

export const setClass = (el: Element, cls: string, on: boolean): void => {
  if (el.classList.contains(cls) !== on) {
    el.classList.toggle(cls, on);
  }
};

export const setChecked = (input: HTMLInputElement, on: boolean): void => {
  if (input.checked !== on) {
    input.checked = on;
  }
};

export const setDisabled = (
  input: HTMLInputElement | HTMLButtonElement,
  disabled: boolean,
): void => {
  if (input.disabled !== disabled) {
    input.disabled = disabled;
  }
};

export const createFrame = (render: () => void): (() => void) => {
  let scheduled = false;
  const frame = (): void => {
    scheduled = false;
    render();
  };
  return () => {
    if (!scheduled) {
      scheduled = true;
      requestAnimationFrame(frame);
    }
  };
};
