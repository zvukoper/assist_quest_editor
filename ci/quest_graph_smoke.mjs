    return new DOMPoint(x, y).matrixTransform(svg.getScreenCTM().inverse());
  }, { x: endX, y: endY });

  const match = pendingPath.match(/\s([-\d.]+)\s([-\d.]+)$/);
  if (!match) {
    throw new Error("Не удалось разобрать конечную точку preview кабеля: " + pendingPath);
  }

  const actualX = Number(match[1]);
  const actualY = Number(match[2]);
  if (Math.abs(actualX - expectedPoint.x) > 0.01 || Math.abs(actualY - expectedPoint.y) > 0.01) {
    throw new Error(
      "Preview кабеля не совпадает с курсором: actual=(" +
      actualX + "," + actualY + "), expected=(" +
      expectedPoint.x + "," + expectedPoint.y + ")"
    );
  }
